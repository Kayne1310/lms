using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Services;

public sealed class WorkflowLogEntry
{
    public string Time { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Type { get; init; } = "info"; // info, work, success, warning, error, highlight

    public WorkflowLogEntry() { }

    public WorkflowLogEntry(string time, string tag, string message, string type = "info")
    {
        Time = time;
        Tag = tag;
        Message = message;
        Type = type;
    }
}

public sealed class HarvestWorkflowState
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Guid AccountId { get; init; }
    public string Status { get; set; } = "running"; // running, completed, failed
    public int ProgressPct { get; set; } = 0;
    public string CurrentStep { get; set; } = "Đang khởi tạo...";
    public string? CurrentCourseName { get; set; }
    public List<WorkflowLogEntry> Logs { get; } = new();
    public int DocsCompleted { get; set; }
    public int ReflectionsSubmitted { get; set; }
    public int PostsPublished { get; set; }
    public int QuizzesSolved { get; set; }
    public decimal TotalLpGained { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public void AddLog(string tag, string message, string type = "info")
    {
        lock (Logs)
        {
            Logs.Add(new WorkflowLogEntry(DateTimeOffset.UtcNow.ToString("HH:mm:ss"), tag, message, type));
        }
    }
}

public sealed class LpHarvestWorkflowService
{
    private static readonly ConcurrentDictionary<string, HarvestWorkflowState> Workflows = new();
    private static readonly ConcurrentDictionary<Guid, string> ActiveAccountWorkflows = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LpHarvestWorkflowService> _logger;

    public LpHarvestWorkflowService(IServiceScopeFactory scopeFactory, ILogger<LpHarvestWorkflowService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public HarvestWorkflowState? GetWorkflow(string workflowId)
    {
        Workflows.TryGetValue(workflowId, out var state);
        return state;
    }

    public HarvestWorkflowState? GetActiveWorkflowForAccount(Guid accountId)
    {
        if (ActiveAccountWorkflows.TryGetValue(accountId, out var workflowId) &&
            Workflows.TryGetValue(workflowId, out var state))
        {
            return state;
        }
        return null;
    }

    public HarvestWorkflowState StartWorkflow(Guid accountId, Guid? courseId)
    {
        // Nếu đã có workflow đang chạy cho account này, trả về workflow đó luôn
        if (ActiveAccountWorkflows.TryGetValue(accountId, out var existingId) &&
            Workflows.TryGetValue(existingId, out var existingState) &&
            existingState.Status == "running")
        {
            return existingState;
        }

        var state = new HarvestWorkflowState
        {
            AccountId = accountId,
            Status = "running",
            ProgressPct = 2,
            CurrentStep = "Đang kết nối hệ thống và chuẩn bị workflow..."
        };

        Workflows[state.Id] = state;
        ActiveAccountWorkflows[accountId] = state.Id;

        // Chạy ngầm trong background với Task độc lập
        _ = Task.Run(async () =>
        {
            try
            {
                await RunWorkflowCoreAsync(state, accountId, courseId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong Harvest Workflow {WorkflowId}", state.Id);
                state.Status = "failed";
                state.ErrorMessage = ex.Message;
                state.AddLog("ERROR", $"Workflow gặp lỗi: {ex.Message}", "error");
            }
            finally
            {
                state.FinishedAt = DateTimeOffset.UtcNow;
                if (state.Status == "running") state.Status = "completed";
            }
        });

        return state;
    }

    private async Task RunWorkflowCoreAsync(HarvestWorkflowState state, Guid accountId, Guid? courseId)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        var eduso = scope.ServiceProvider.GetRequiredService<EdusoWidgetClient>();
        var accountSessions = scope.ServiceProvider.GetRequiredService<EdusoAccountSessionService>();

        state.AddLog("INIT", "Khởi tạo môi trường workflow tự động...", "info");

        var account = await db.StudentAccounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => new { x.Id, x.Email })
            .SingleOrDefaultAsync();

        if (account is null)
        {
            state.Status = "failed";
            state.ErrorMessage = "Tài khoản sinh viên không tồn tại.";
            state.AddLog("ERROR", "Không tìm thấy tài khoản sinh viên trong cơ sở dữ liệu.", "error");
            return;
        }

        state.AddLog("AUTH", $"Đang xác thực phiên Eduso cho tài khoản {account.Email}...", "work");

        string token;
        try
        {
            token = await accountSessions.CreateSessionForAccountAsync(account.Id, CancellationToken.None);
            state.AddLog("AUTH", "✅ Đã xác thực Eduso thành công với mật khẩu từ cơ sở dữ liệu.", "success");
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.ErrorMessage = ex.Message;
            state.AddLog("AUTH", $"❌ Lỗi xác thực tài khoản: {ex.Message}", "error");
            return;
        }

        // Lấy danh sách môn cần cày
        var query = db.Courses.AsNoTracking().Where(x => x.StudentAccountId == account.Id);
        if (courseId.HasValue)
        {
            query = query.Where(x => x.Id == courseId.Value);
        }
        var courses = await query.OrderBy(x => x.Code).ToListAsync();

        if (courses.Count == 0)
        {
            state.Status = "failed";
            state.ErrorMessage = "Không tìm thấy môn học nào cần xử lý.";
            state.AddLog("WARNING", "Chưa có môn học nào được đồng bộ. Hãy bấm 'Đồng bộ môn' trước.", "warning");
            return;
        }

        state.AddLog("WORKFLOW", $"Tìm thấy {courses.Count} môn học trong danh sách xử lý.", "info");

        int totalCourses = courses.Count;
        int courseIdx = 0;

        foreach (var course in courses)
        {
            courseIdx++;
            state.CurrentCourseName = course.Name;
            state.CurrentStep = $"Đang xử lý môn {courseIdx}/{totalCourses}: {course.Name}";
            state.ProgressPct = 5 + (int)((courseIdx - 1.0) / totalCourses * 90);

            state.AddLog("COURSE", $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━", "highlight");
            state.AddLog("COURSE", $"▶ [{courseIdx}/{totalCourses}] {course.Code} - {course.Name}", "highlight");

            try
            {
                await HarvestSingleCourseWorkflowAsync(state, db, eduso, account.Id, course, token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi cày môn {CourseCode}", course.Code);
                state.AddLog("COURSE", $"⚠️ Lỗi khi cày môn {course.Name}: {ex.Message}", "warning");
            }
        }

        state.ProgressPct = 100;
        state.CurrentStep = "Hoàn tất toàn bộ workflow!";
        state.Status = "completed";
        state.AddLog("SYSTEM", $"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━", "highlight");
        state.AddLog("SYSTEM", $"🎉 TỔNG KẾT WORKFLOW HOÀN THÀNH XUẤT SẮC!", "success");
        state.AddLog("SYSTEM", $"📊 Tài liệu đã đọc (LP1): {state.DocsCompleted} | Nhật ký (LP4): {state.ReflectionsSubmitted} | Thảo luận (LP5): {state.PostsPublished} | Quiz & Bài thi đã giải: {state.QuizzesSolved}", "highlight");
        state.AddLog("SYSTEM", $"⭐ Tổng điểm LP gia tăng: +{state.TotalLpGained} LP", "success");
    }

    private async Task HarvestSingleCourseWorkflowAsync(
        HarvestWorkflowState state,
        AttendanceDbContext db,
        EdusoWidgetClient eduso,
        Guid accountId,
        Course course,
        string token)
    {
        var remoteCourseId = course.RemoteCourseId;
        await eduso.SelectCourseAsync(remoteCourseId, token, CancellationToken.None);

        decimal? lpBefore = null;
        try
        {
            using var ovBefore = await eduso.GetCourseOverviewAsync(remoteCourseId, token, CancellationToken.None);
            if (ovBefore.RootElement.TryGetProperty("data", out var ovData) &&
                ovData.TryGetProperty("total_lp", out var lpEl) &&
                lpEl.TryGetDecimal(out var val))
            {
                lpBefore = val;
                state.AddLog("LP", $"Điểm LP hiện tại của môn: {lpBefore} LP", "info");
            }
        }
        catch { }

        // Tải Learning Plan
        state.AddLog("PLAN", "Đang quét cây kế hoạch học tập (Learning Plan)...", "work");
        using var planDoc = await eduso.GetCourseLearningPlanAsync(remoteCourseId, token, CancellationToken.None);

        if (!planDoc.RootElement.TryGetProperty("data", out var planData) ||
            !planData.TryGetProperty("semesters", out var semestersEl) ||
            semestersEl.ValueKind != JsonValueKind.Array)
        {
            state.AddLog("PLAN", "Môn này chưa có cấu hình thời khóa biểu bài học.", "warning");
            return;
        }

        foreach (var semester in semestersEl.EnumerateArray())
        {
            if (!semester.TryGetProperty("timeline", out var timelineEl) || timelineEl.ValueKind != JsonValueKind.Array) continue;

            foreach (var week in timelineEl.EnumerateArray())
            {
                var weekNumber = week.TryGetProperty("week_number", out var wnProp) && wnProp.TryGetInt32(out var wn) ? wn : 1;
                if (!week.TryGetProperty("lessons", out var lessonsEl) || lessonsEl.ValueKind != JsonValueKind.Array) continue;

                foreach (var lesson in lessonsEl.EnumerateArray())
                {
                    var lessonId = lesson.TryGetProperty("id", out var lidProp) ? lidProp.GetString() : null;
                    var lessonTitle = lesson.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "Bài học";
                    var lessonType = lesson.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : "lesson";
                    if (string.IsNullOrEmpty(lessonId)) continue;

                    if (lessonType == "lesson")
                    {
                        var docIds = new List<string>();

                        // 1. Hoàn thành đọc tài liệu (LP1) & Giải Quiz tài liệu nếu có
                        if (lesson.TryGetProperty("docs_info", out var docsInfoEl) && docsInfoEl.ValueKind == JsonValueKind.Array && docsInfoEl.GetArrayLength() > 0)
                        {
                            foreach (var d in docsInfoEl.EnumerateArray())
                            {
                                var did = d.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                var dTitle = d.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
                                var hasQuiz = d.TryGetProperty("has_quiz", out var hqProp) && hqProp.GetBoolean();
                                if (string.IsNullOrEmpty(did)) continue;
                                docIds.Add(did);

                                var displayName = dTitle ?? did.Substring(Math.Max(0, did.Length - 8));
                                state.AddLog("LP1", $"[Tuần {weekNumber}] Đang đọc tài liệu {displayName}... (100%)", "work");
                                var ok = await eduso.CompleteDocReadingAsync(remoteCourseId, lessonId, did, dTitle, token, CancellationToken.None);
                                if (ok)
                                {
                                    state.DocsCompleted++;
                                    state.AddLog("LP1", $"✅ Đã xem xong tài liệu {displayName} (LP1)", "success");
                                }
                                await Task.Delay(200);

                                if (hasQuiz)
                                {
                                    state.AddLog("QUIZ-DOC", $"[Tuần {weekNumber}] ❓ Phát hiện Quiz tài liệu: {displayName}. Bắt đầu giải thích nghi...", "work");
                                    var docQuizRes = await eduso.SolveDocumentQuizAsync(remoteCourseId, lessonId, did, token, CancellationToken.None);
                                    if (docQuizRes.Success)
                                    {
                                        state.QuizzesSolved++;
                                        state.AddLog("QUIZ-DOC", $"🎯 [{displayName}] Đã vượt qua Quiz tài liệu ({docQuizRes.Score}/{docQuizRes.Total} điểm! Passed: {docQuizRes.Passed})", "success");
                                    }
                                    else
                                    {
                                        state.AddLog("QUIZ-DOC", $"⚠️ [{displayName}] Quiz tài liệu: {docQuizRes.Message}", "warning");
                                    }
                                    await Task.Delay(250);
                                }
                            }
                        }
                        else if (lesson.TryGetProperty("document_ids", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var d in docsEl.EnumerateArray())
                            {
                                var did = d.GetString();
                                if (string.IsNullOrEmpty(did)) continue;
                                docIds.Add(did);

                                state.AddLog("LP1", $"[Tuần {weekNumber}] Đang đọc tài liệu {did.Substring(Math.Max(0, did.Length - 8))}... (100%)", "work");
                                var ok = await eduso.CompleteDocReadingAsync(remoteCourseId, lessonId, did, null, token, CancellationToken.None);
                                if (ok)
                                {
                                    state.DocsCompleted++;
                                    state.AddLog("LP1", $"✅ Đã xem xong tài liệu {did.Substring(Math.Max(0, did.Length - 8))} (LP1)", "success");
                                }
                                await Task.Delay(200);
                            }
                        }

                        // 2. Đăng bài thảo luận diễn đàn (LP5 - 3 LP)
                        if (docIds.Count > 0)
                        {
                            state.AddLog("LP5", $"[Tuần {weekNumber} - {lessonTitle}] Đang gửi bài thảo luận học tập (+3 LP)...", "work");
                            var postOk = await eduso.PostDiscussionAsync(remoteCourseId, lessonId, docIds, token, CancellationToken.None);
                            if (postOk)
                            {
                                state.PostsPublished++;
                                state.AddLog("LP5", $"✅ Đã đăng bài thảo luận thành công (+3 LP)", "success");
                            }
                            await Task.Delay(200);
                        }

                        // 3. Nộp nhật ký phản xạ học tập (LP4 - 8 LP)
                        state.AddLog("LP4", $"[Tuần {weekNumber} - {lessonTitle}] Đang nộp nhật ký học tập (+8 LP)...", "work");
                        var refRes = await eduso.SubmitLp4ReflectionAsync(remoteCourseId, lessonId, weekNumber, token, CancellationToken.None);
                        if (refRes.Success && !refRes.AlreadySubmitted)
                        {
                            state.ReflectionsSubmitted++;
                            state.AddLog("LP4", $"✅ Nộp thành công nhật ký phản xạ tuần {weekNumber} (+8 LP)", "success");
                        }
                        else if (refRes.AlreadySubmitted)
                        {
                            state.AddLog("LP4", $"ℹ️ Tuần {weekNumber} đã có nhật ký từ trước.", "info");
                        }
                        await Task.Delay(200);

                        // 4. Giải Quiz bài học (tab "Quiz" của bài học)
                        state.AddLog("QUIZ-LESSON", $"[Tuần {weekNumber} - {lessonTitle}] 📝 Đang kiểm tra & giải Quiz bài học (tab Quiz)...", "work");
                        var lessonQuizRes = await eduso.SolveLessonQuizAsync(remoteCourseId, lessonId, token, CancellationToken.None);
                        if (lessonQuizRes.Success)
                        {
                            state.QuizzesSolved++;
                            state.AddLog("QUIZ-LESSON", $"🏆 [{lessonTitle}] Đã hoàn thành Quiz bài học ({lessonQuizRes.Score}/{lessonQuizRes.Total} điểm! Passed: {lessonQuizRes.Passed})", "success");
                        }
                        else
                        {
                            state.AddLog("QUIZ-LESSON", $"⚠️ [{lessonTitle}] Quiz bài học: {lessonQuizRes.Message}", "warning");
                        }
                        await Task.Delay(250);
                    }
                    else if (lessonType == "exam")
                    {
                        // 4. Giải bài kiểm tra (LP2 Exam Quiz)
                        if (lesson.TryGetProperty("exam_config", out var examCfg) && examCfg.ValueKind == JsonValueKind.Object)
                        {
                            state.AddLog("EXAM", $"[Tuần {weekNumber} - {lessonTitle}] Phát hiện bài thi trắc nghiệm. Đang trích xuất đáp án...", "work");
                            var quizRes = await eduso.SolveExamQuizAsync(remoteCourseId, lessonId, examCfg, token, CancellationToken.None);
                            if (quizRes.Success)
                            {
                                state.QuizzesSolved++;
                                state.AddLog("EXAM", $"🎯 Giải bài kiểm tra thành công: {quizRes.Score}/{quizRes.Total} điểm! (Trạng thái: {(quizRes.Passed ? "ĐẠT" : "HOÀN THÀNH")})", "success");
                            }
                            else
                            {
                                state.AddLog("EXAM", $"⚠️ Bài kiểm tra tuần {weekNumber}: {quizRes.Message}", "warning");
                            }
                            await Task.Delay(300);
                        }
                    }
                }
            }
        }

        // Lấy lại điểm số sau khi cày
        decimal? lpAfter = null;
        decimal? ceuAfter = null;
        try
        {
            using var ovAfter = await eduso.GetCourseOverviewAsync(remoteCourseId, token, CancellationToken.None);
            if (ovAfter.RootElement.TryGetProperty("data", out var dAfter))
            {
                if (dAfter.TryGetProperty("total_lp", out var lpA) && lpA.TryGetDecimal(out var valA)) lpAfter = valA;
                if (dAfter.TryGetProperty("total_ceu", out var ceuA) && ceuA.TryGetDecimal(out var valC)) ceuAfter = valC;
            }
        }
        catch { }

        var delta = (lpAfter ?? 0) - (lpBefore ?? 0);
        if (delta > 0) state.TotalLpGained += delta;

        state.AddLog("RESULT", $"Môn {course.Name} đã cày xong: LP = {lpBefore ?? 0} ➔ {lpAfter ?? 0} (Tăng: +{delta} LP). CEU = {ceuAfter ?? 0}", "highlight");

        // Đồng bộ kết quả vào LearningProgressChecks
        try
        {
            using var plan = await eduso.GetCourseLearningPlanAsync(remoteCourseId, token, CancellationToken.None);
            using var ov = await eduso.GetCourseOverviewAsync(remoteCourseId, token, CancellationToken.None);
            
            db.LearningProgressChecks.Add(new LearningProgressCheck
            {
                StudentAccountId = accountId,
                CourseId = course.Id,
                CourseCode = course.Code,
                CourseName = course.Name,
                Status = "success",
                Message = $"Workflow cày LP hoàn tất: LP = {lpAfter ?? 0} (+{delta} LP).",
                TotalLp = lpAfter,
                DeltaLp = delta,
                TotalCeu = ceuAfter,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không thể lưu LearningProgressCheck");
        }
    }
}

