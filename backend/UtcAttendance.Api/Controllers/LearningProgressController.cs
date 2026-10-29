using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;
using UtcAttendance.Api.Services;

namespace UtcAttendance.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/learning-progress")]
public sealed class LearningProgressController(
    AttendanceDbContext db,
    EdusoWidgetClient eduso,
    EdusoAccountSessionService accountSessions,
    LpHarvestWorkflowService workflows) : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public async Task<ActionResult> GetHistory(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var rows = await db.LearningProgressChecks.AsNoTracking()
            .Where(x => x.StudentAccountId == accountId.Value)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        return Ok(ToResponse(rows));
    }

    [HttpPost("sync")]
    public async Task<ActionResult> Sync(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var account = await db.StudentAccounts.AsNoTracking()
            .Where(x => x.Id == accountId.Value)
            .Select(x => new { x.Id, x.Email })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null) return NotFound();

        var courses = await db.Courses.AsNoTracking()
            .Where(x => x.StudentAccountId == account.Id)
            .OrderBy(x => x.Code)
            .Select(x => new { x.Id, x.RemoteCourseId, x.Code, x.Name })
            .ToListAsync(cancellationToken);
        if (courses.Count == 0) return Conflict(new { message = "Hãy đồng bộ môn học trước khi kiểm tra tiến độ." });

        var previousRows = await db.LearningProgressChecks.AsNoTracking()
            .Where(x => x.StudentAccountId == account.Id && x.Status == "success")
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(500)
            .ToListAsync(cancellationToken);
        var previousByCourse = previousRows
            .GroupBy(x => x.CourseId)
            .ToDictionary(group => group.Key, group => group.First().TotalLp);

        string? token = null;
        try
        {
            token = await accountSessions.CreateSessionForAccountAsync(account.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            foreach (var course in courses)
                db.LearningProgressChecks.Add(CreateFailure(account.Id, course.Id, course.Code, course.Name,
                    "Không tạo được phiên Eduso để đọc tiến độ. Kiểm tra cấu hình đăng nhập rồi thử lại."));
            await db.SaveChangesAsync(cancellationToken);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { message = "Không tạo được phiên Eduso. Trạng thái lỗi đã được lưu vào lịch sử." });
        }

        using var concurrency = new SemaphoreSlim(4, 4);
        var results = new ConcurrentBag<LearningProgressCheck>();
        await Task.WhenAll(courses.Select(async course =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                using var plan = await eduso.GetCourseLearningPlanAsync(course.RemoteCourseId, token, cancellationToken);
                using var overview = await eduso.GetCourseOverviewAsync(course.RemoteCourseId, token, cancellationToken);
                var progress = ParseCourseProgress(plan.RootElement, overview.RootElement);
                var previousTotal = previousByCourse.GetValueOrDefault(course.Id);
                results.Add(new LearningProgressCheck
                {
                    StudentAccountId = account.Id,
                    CourseId = course.Id,
                    CourseCode = course.Code,
                    CourseName = course.Name,
                    Status = "success",
                    Message = "Đã đọc tiến độ từ Eduso.",
                    TotalLp = progress.TotalLp,
                    DeltaLp = previousTotal is not null && progress.TotalLp is not null
                        ? progress.TotalLp.Value - previousTotal.Value
                        : null,
                    TotalCeu = progress.TotalCeu,
                    ProgressPct = progress.ProgressPct,
                    LpByTypeJson = JsonSerializer.Serialize(progress.LpByType, Json),
                    LessonsJson = JsonSerializer.Serialize(progress.Lessons, Json),
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException exception)
            {
                var message = exception.StatusCode is { } status
                    ? $"Eduso từ chối đọc tiến độ (HTTP {(int)status})."
                    : "Không kết nối được Eduso khi đọc tiến độ.";
                results.Add(CreateFailure(account.Id, course.Id, course.Code, course.Name, message));
            }
            catch (JsonException)
            {
                results.Add(CreateFailure(account.Id, course.Id, course.Code, course.Name,
                    "Eduso trả về dữ liệu tiến độ không đúng định dạng."));
            }
            catch (Exception ex)
            {
                results.Add(CreateFailure(account.Id, course.Id, course.Code, course.Name,
                    $"Lỗi xử lý dữ liệu tiến độ: {ex.Message}"));
            }
            finally
            {
                concurrency.Release();
            }
        }));

        db.LearningProgressChecks.AddRange(results);
        await db.SaveChangesAsync(cancellationToken);
        var rows = await db.LearningProgressChecks.AsNoTracking()
            .Where(x => x.StudentAccountId == account.Id)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);
        return Ok(ToResponse(rows));
    }

    [HttpPost("harvest/{courseId}")]
    public async Task<ActionResult> HarvestCourse(Guid courseId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var course = await db.Courses.AsNoTracking()
            .Where(x => x.Id == courseId && x.StudentAccountId == accountId.Value)
            .Select(x => new { x.Id, x.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (course is null) return NotFound(new { message = "Không tìm thấy môn học." });

        var state = workflows.StartWorkflow(accountId.Value, course.Id);

        return Ok(new
        {
            workflowId = state.Id,
            message = $"Đã khởi động workflow cày LP môn {course.Name}.",
            state
        });
    }

    [HttpPost("harvest-all")]
    public ActionResult HarvestAll()
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var state = workflows.StartWorkflow(accountId.Value, null);

        return Ok(new
        {
            workflowId = state.Id,
            message = "Đã khởi động workflow cày LP tất cả các môn học.",
            state
        });
    }

    [HttpGet("workflow/{workflowId}")]
    public ActionResult GetWorkflow(string workflowId)
    {
        var state = workflows.GetWorkflow(workflowId);
        if (state is null) return NotFound(new { message = "Không tìm thấy workflow." });
        return Ok(state);
    }

    [HttpGet("workflow/active")]
    public ActionResult GetActiveWorkflow()
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var state = workflows.GetActiveWorkflowForAccount(accountId.Value);
        return Ok(new { active = state is not null && state.Status == "running", state });
    }

    private async Task SyncCourseProgressAsync(Guid accountId, Guid courseId, string remoteCourseId, string code, string name, string token, CancellationToken cancellationToken)
    {
        try
        {
            var previousCheck = await db.LearningProgressChecks.AsNoTracking()
                .Where(x => x.StudentAccountId == accountId && x.CourseId == courseId && x.Status == "success")
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            using var plan = await eduso.GetCourseLearningPlanAsync(remoteCourseId, token, cancellationToken);
            using var overview = await eduso.GetCourseOverviewAsync(remoteCourseId, token, cancellationToken);
            var progress = ParseCourseProgress(plan.RootElement, overview.RootElement);

            db.LearningProgressChecks.Add(new LearningProgressCheck
            {
                StudentAccountId = accountId,
                CourseId = courseId,
                CourseCode = code,
                CourseName = name,
                Status = "success",
                Message = "Đã tự động làm bài tập & cày LP thành công.",
                TotalLp = progress.TotalLp,
                DeltaLp = previousCheck?.TotalLp is not null && progress.TotalLp is not null
                    ? progress.TotalLp.Value - previousCheck.TotalLp.Value
                    : null,
                TotalCeu = progress.TotalCeu,
                ProgressPct = progress.ProgressPct,
                LpByTypeJson = JsonSerializer.Serialize(progress.LpByType, Json),
                LessonsJson = JsonSerializer.Serialize(progress.Lessons, Json),
                CreatedAtUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch { }
    }

    private static LearningProgressCheck CreateFailure(Guid accountId, Guid courseId, string code, string name, string message)
        => new()
        {
            StudentAccountId = accountId,
            CourseId = courseId,
            CourseCode = code,
            CourseName = name,
            Status = "error",
            Message = message,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

    private static object ToResponse(IReadOnlyList<LearningProgressCheck> rows)
    {
        var latest = rows.GroupBy(x => x.CourseId).Select(group => group.First()).ToArray();
        return new
        {
            latest = latest.Select(ToItem),
            history = rows.Select(ToItem),
        };
    }

    private static object ToItem(LearningProgressCheck row) => new
    {
        row.Id,
        row.CourseId,
        row.CourseCode,
        row.CourseName,
        row.Status,
        row.Message,
        row.TotalLp,
        row.DeltaLp,
        row.TotalCeu,
        row.ProgressPct,
        lpByType = ParseJsonOrNull(row.LpByTypeJson),
        lessons = ParseJsonOrNull(row.LessonsJson),
        row.CreatedAtUtc,
    };

    private static JsonElement? ParseJsonOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CourseProgress ParseCourseProgress(JsonElement planRoot, JsonElement overviewRoot)
    {
        ValidateEnvelope(planRoot);
        ValidateEnvelope(overviewRoot);
        var plan = GetObject(planRoot, "data");
        var overview = GetObject(overviewRoot, "data");
        if (plan.ValueKind != JsonValueKind.Object || overview.ValueKind != JsonValueKind.Object)
            throw new JsonException("Eduso progress response is missing data.");
        var totalLp = GetDecimal(overview, "total_lp");
        if (totalLp is null) throw new JsonException("Eduso overview is missing total_lp.");
        var lessons = new List<LessonProgress>();
        if (TryGetArray(plan, "semesters", out var semesters))
        {
            foreach (var semester in semesters.EnumerateArray())
            {
                if (!TryGetArray(semester, "timeline", out var timeline)) continue;
                foreach (var week in timeline.EnumerateArray())
                {
                    var weekNumber = GetInt(week, "week_number");
                    if (!TryGetArray(week, "lessons", out var weekLessons)) continue;
                    foreach (var lesson in weekLessons.EnumerateArray())
                    {
                        var lessonId = GetString(lesson, "id");
                        if (string.IsNullOrWhiteSpace(lessonId)) continue;
                        lessons.Add(new LessonProgress(
                            lessonId,
                            GetString(lesson, "title") ?? "Bài học",
                            GetString(lesson, "type"),
                            weekNumber,
                            GetDecimal(lesson, "completion_pct"),
                            GetInt(lesson, "quiz_attempt_count"),
                            GetDecimal(lesson, "quiz_best_score"),
                            GetBool(lesson, "completed"),
                            GetBool(lesson, "is_locked")));
                    }
                }
            }
        }

        var lpByType = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var rawLpByType = GetObject(overview, "lp_by_type");
        if (rawLpByType.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in rawLpByType.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDecimal(out var points))
                {
                    lpByType[property.Name] = points;
                }
            }
        }

        return new CourseProgress(
            totalLp,
            GetDecimal(overview, "total_ceu"),
            GetDecimal(overview, "progress_pct"),
            lpByType,
            lessons);
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var success)
            || success.ValueKind != JsonValueKind.True)
            throw new JsonException("Eduso response did not confirm success.");
    }

    private static JsonElement GetObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : default;

    private static bool TryGetArray(JsonElement parent, string name, out JsonElement array)
    {
        array = GetObject(parent, name);
        return array.ValueKind == JsonValueKind.Array;
    }

    private static string? GetString(JsonElement parent, string name)
    {
        var value = GetObject(parent, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int? GetInt(JsonElement parent, string name)
    {
        var value = GetObject(parent, name);
        if (value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    private static decimal? GetDecimal(JsonElement parent, string name)
    {
        var value = GetObject(parent, name);
        if (value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetDecimal(out var number) ? number : null;
    }

    private static bool? GetBool(JsonElement parent, string name)
    {
        var value = GetObject(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private Guid? CurrentAccountId()
        => Guid.TryParse(User.FindFirst("sub")?.Value, out var accountId) ? accountId : null;

    private sealed record CourseProgress(
        decimal? TotalLp,
        decimal? TotalCeu,
        decimal? ProgressPct,
        Dictionary<string, decimal> LpByType,
        List<LessonProgress> Lessons);

    private sealed record LessonProgress(
        string Id,
        string Title,
        string? Type,
        int? WeekNumber,
        decimal? CompletionPct,
        int? QuizAttemptCount,
        decimal? QuizBestScore,
        bool? Completed,
        bool? IsLocked);
}
