using System.Net.Http.Json;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace UtcAttendance.Api.Services;

public sealed class EdusoOptions
{
    public const string SectionName = "Eduso";
    public string OrganizationId { get; init; } = "utc";
}

public sealed class EdusoWidgetClient(HttpClient http, IOptions<EdusoOptions> options, ILogger<EdusoWidgetClient> logger)
{
    private readonly EdusoOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> CreateSessionAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("An Eduso password is required.", nameof(password));

        var curlCommand = CreateSessionCurlTemplate();
        try
        {
            using var response = await http.PostAsJsonAsync("widget/sessions", new
            {
                email, password, role = "student", org_id = _options.OrganizationId
            }, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Eduso rejected session creation for {Email} with HTTP {StatusCode} ({ReasonPhrase}).",
                    MaskEmail(email), (int)response.StatusCode, response.ReasonPhrase);
                throw new EdusoApiException(
                    "Eduso rejected the session request.", response.StatusCode,
                    RedactSensitiveResponse(responseText), curlCommand);
            }

            WidgetSession? session;
            try
            {
                session = JsonSerializer.Deserialize<WidgetSession>(responseText, Json);
            }
            catch (JsonException exception)
            {
                throw new EdusoApiException("Eduso returned an invalid session response.", response.StatusCode,
                    RedactSensitiveResponse(responseText), curlCommand, exception);
            }

            if (string.IsNullOrWhiteSpace(session?.SessionToken))
                throw new EdusoApiException("Eduso session response did not contain session_token.", response.StatusCode,
                    RedactSensitiveResponse(responseText), curlCommand);
            return session.SessionToken;
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
            logger.LogWarning(exception, "Unable to create an Eduso session for {Email}.", MaskEmail(email));
            if (exception is EdusoApiException) throw;
            throw new EdusoApiException("Unable to connect to Eduso while creating a session.", null,
                rawResponse: null, curlCommand: curlCommand, innerException: exception);
        }
    }

    public static string CreateSessionCurlTemplate()
        => "curl --request POST 'https://aiapi.eduso.vn/api/v1/widget/sessions' " +
           "--header 'content-type: application/json' " +
           "--data-raw '{\"email\":\"<EDUSO_EMAIL>\",\"password\":\"<EDUSO_PASSWORD>\",\"role\":\"student\",\"org_id\":\"utc\"}'";

    private static string RedactSensitiveResponse(string response)
        => Regex.Replace(response,
            "(?i)(\\\"(?:password|session_token|access_token|refresh_token|token|authorization|secret)\\\"\\s*:\\s*\\\")[^\\\"]*(\\\")",
            "$1[REDACTED]$2");

    public async Task<IReadOnlyList<RemoteCourse>> GetCoursesAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorize(HttpMethod.Get, "widget/courses", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<CourseEnvelope>(Json, cancellationToken);
        return envelope?.Data?.Courses ?? [];
    }

    public async Task<IReadOnlyList<RemoteLiveSession>> GetLiveSessionsAsync(string courseId, string token, CancellationToken cancellationToken)
    {
        using var request = Authorize(HttpMethod.Get, $"widget/courses/{courseId}/live-sessions", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<LiveEnvelope>(Json, cancellationToken);
        return envelope?.Data ?? [];
    }

    public Task<JsonDocument> GetCourseLearningPlanAsync(string courseId, string token, CancellationToken cancellationToken)
        => GetJsonAsync($"widget/courses/{Uri.EscapeDataString(courseId)}/learning-plan", token, cancellationToken);

    public Task<JsonDocument> GetCourseOverviewAsync(string courseId, string token, CancellationToken cancellationToken)
        => GetJsonAsync($"widget/courses/{Uri.EscapeDataString(courseId)}/overview", token, cancellationToken);

    public async Task SelectCourseAsync(string courseId, string token, CancellationToken cancellationToken)
    {
        using var request = Authorize(HttpMethod.Post, "widget/select-course", token);
        request.Content = JsonContent.Create(new { course_id = courseId });
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> CompleteDocReadingAsync(string courseId, string lessonId, string docId, string? docName, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using var request = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/log-activity", token);
            request.Content = JsonContent.Create(new
            {
                activity_type = "document_view",
                lesson_id = lessonId,
                resource_id = docId,
                resource_name = docName ?? $"doc_{docId}.pptx",
                duration_seconds = 60,
                details = new
                {
                    pages_viewed = Enumerable.Range(1, 25).ToArray(),
                    completion_pct = 100
                }
            });
            using var response = await http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[LP1] Đã xem xong tài liệu {DocId} ({DocName}).", docId, docName ?? docId);
                return true;
            }
            logger.LogWarning("[LP1] Xem tài liệu {DocId} trả về HTTP {StatusCode}.", docId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[LP1] Xem tài liệu {DocId} bị timeout sau 20s (bỏ qua để tiếp tục).", docId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LP1] Lỗi khi hoàn thành xem tài liệu {DocId}.", docId);
            return false;
        }
    }

    public async Task<(bool Success, bool AlreadySubmitted, string Message)> SubmitLp4ReflectionAsync(string courseId, string lessonId, int weekNumber, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var chkCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            chkCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var chkReq = Authorize(HttpMethod.Get, $"widget/lessons/{lessonId}/lp4-status?course_id={courseId}", token);
            using var chkRes = await http.SendAsync(chkReq, chkCts.Token);
            if (chkRes.IsSuccessStatusCode)
            {
                using var chkDoc = await JsonDocument.ParseAsync(await chkRes.Content.ReadAsStreamAsync(chkCts.Token), cancellationToken: chkCts.Token);
                if (chkDoc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("already_submitted", out var sub) && sub.GetBoolean())
                {
                    logger.LogInformation("[LP4] Bài {LessonId} tuần {Week} đã nộp phản xạ trước đó.", lessonId, weekNumber);
                    return (true, true, "Đã nộp nhật ký trước đó.");
                }
            }
        }
        catch { /* continue to submit */ }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            using var request = Authorize(HttpMethod.Post, $"widget/lessons/{lessonId}/lp4-reflection", token);
            request.Content = JsonContent.Create(new
            {
                course_id = courseId,
                week_number = weekNumber,
                answers = new[]
                {
                    "Sau bài học này tôi đã nắm vững toàn bộ các kiến thức trọng tâm và định nghĩa cơ bản.",
                    "Nội dung bài học được trình bày rất rõ ràng và dễ tiếp thu, hiện tại tôi chưa có thắc mắc nào.",
                    "Tôi sẽ chú ý theo dõi bài giảng live session của thầy/cô để củng cố thêm các dạng bài tập thực hành."
                }
            });
            using var response = await http.SendAsync(request, cts.Token);
            var success = response.IsSuccessStatusCode;
            if (success)
            {
                logger.LogInformation("[LP4] Đã nộp thành công nhật ký phản xạ tuần {Week} (+8 LP) cho bài {LessonId}.", weekNumber, lessonId);
            }
            else
            {
                logger.LogWarning("[LP4] Nộp nhật ký tuần {Week} bài {LessonId} trả về HTTP {StatusCode}.", weekNumber, lessonId, (int)response.StatusCode);
            }
            return (success, false, success ? "Đã nộp thành công nhật ký học tập (LP4)." : "Không thể nộp nhật ký.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[LP4] Nộp nhật ký tuần {Week} bài {LessonId} bị timeout sau 25s (bỏ qua tiếp tục).", weekNumber, lessonId);
            return (false, false, "Timeout khi nộp nhật ký.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LP4] Lỗi nộp nhật ký tuần {Week} bài {LessonId}.", weekNumber, lessonId);
            return (false, false, "Lỗi kết nối khi nộp nhật ký.");
        }
    }

    public async Task<bool> PostDiscussionAsync(string courseId, string lessonId, IReadOnlyList<string> docIds, string token, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(25));

            using var request = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/feed/posts", token);
            request.Content = JsonContent.Create(new
            {
                content = "Bài học cung cấp nhiều kiến thức hữu ích và thực tiễn. Cần chú ý ôn tập kỹ các định lý và khái niệm nền tảng trong tài liệu để áp dụng tốt vào các bài tập và bài kiểm tra định kỳ.",
                lesson_id = lessonId,
                visibility = "public",
                contribution_type = "",
                links = Array.Empty<string>(),
                attachments = Array.Empty<string>(),
                source_doc_ids = docIds
            });
            using var response = await http.SendAsync(request, cts.Token);
            if (response.IsSuccessStatusCode)
            {
                logger.LogInformation("[LP5] Đã đăng bài thảo luận thành công (+3 LP) cho bài học {LessonId}.", lessonId);
                return true;
            }
            logger.LogWarning("[LP5] Đăng thảo luận bài {LessonId} trả về HTTP {StatusCode}.", lessonId, (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("[LP5] Đăng thảo luận bài {LessonId} bị timeout sau 25s (bỏ qua để tiếp tục).", lessonId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LP5] Lỗi khi đăng thảo luận cho bài học {LessonId}.", lessonId);
            return false;
        }
    }

    public async Task<(bool Success, decimal Score, int Total, bool Passed, string Message)> SolveDocumentQuizAsync(
        string courseId, string lessonId, string docId, string token, CancellationToken cancellationToken, int maxAttempts = 4)
    {
        var answerBank = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        decimal lastScore = 0;
        int lastTotal = 0;
        bool lastPassed = false;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startCts.CancelAfter(TimeSpan.FromSeconds(20));

                using var startReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/documents/{docId}/quiz/start", token);
                startReq.Content = JsonContent.Create(new { });
                using var startRes = await http.SendAsync(startReq, startCts.Token);
                if (!startRes.IsSuccessStatusCode)
                {
                    logger.LogWarning("[QUIZ-DOC] Start quiz tài liệu {DocId} trả về HTTP {StatusCode}.", docId, (int)startRes.StatusCode);
                    if (attempt == 1)
                        return (false, 0, 0, false, $"Không thể bắt đầu quiz tài liệu (HTTP {(int)startRes.StatusCode}).");
                    break;
                }

                using var startDoc = await JsonDocument.ParseAsync(await startRes.Content.ReadAsStreamAsync(startCts.Token), cancellationToken: startCts.Token);
                if (!startDoc.RootElement.TryGetProperty("data", out var dataEl) ||
                    !dataEl.TryGetProperty("questions", out var questionsEl) ||
                    questionsEl.ValueKind != JsonValueKind.Array ||
                    questionsEl.GetArrayLength() == 0)
                {
                    return (false, 0, 0, false, "Tài liệu không có câu hỏi quiz.");
                }

                var submitAnswers = new List<object>();
                foreach (var q in questionsEl.EnumerateArray())
                {
                    var qid = q.TryGetProperty("id", out var qidProp) ? qidProp.GetString() : null;
                    if (string.IsNullOrEmpty(qid)) continue;

                    var qText = q.TryGetProperty("question", out var qtProp) ? qtProp.GetString()?.Trim() : null;

                    string? chosen = null;
                    if (answerBank.TryGetValue(qid, out var a1)) chosen = a1;
                    else if (!string.IsNullOrEmpty(qText) && answerBank.TryGetValue(qText, out var a2)) chosen = a2;

                    if (chosen == null)
                    {
                        if (q.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array && optEl.GetArrayLength() > 0)
                            chosen = optEl[0].GetString();
                        else
                            chosen = "";
                    }

                    submitAnswers.Add(new { question_id = qid, answer = chosen ?? "" });
                }

                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                subCts.CancelAfter(TimeSpan.FromSeconds(25));

                using var subReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/documents/{docId}/quiz/submit", token);
                subReq.Content = JsonContent.Create(new
                {
                    answers = submitAnswers,
                    lesson_id = lessonId
                });

                using var subRes = await http.SendAsync(subReq, subCts.Token);
                if (!subRes.IsSuccessStatusCode)
                {
                    logger.LogWarning("[QUIZ-DOC] Submit quiz tài liệu {DocId} lần {Attempt} trả về HTTP {StatusCode}.", docId, attempt, (int)subRes.StatusCode);
                    break;
                }

                using var subDoc = await JsonDocument.ParseAsync(await subRes.Content.ReadAsStreamAsync(subCts.Token), cancellationToken: subCts.Token);
                if (subDoc.RootElement.TryGetProperty("data", out var resData))
                {
                    lastScore = resData.TryGetProperty("score", out var scProp) && scProp.TryGetDecimal(out var sc) ? sc : 0m;
                    lastTotal = resData.TryGetProperty("total", out var totProp) && totProp.TryGetInt32(out var tot) ? tot : questionsEl.GetArrayLength();
                    lastPassed = resData.TryGetProperty("passed", out var passProp) && passProp.GetBoolean();

                    if (resData.TryGetProperty("questions_review", out var revEl) && revEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in revEl.EnumerateArray())
                        {
                            var rqid = r.TryGetProperty("id", out var rqidProp) ? rqidProp.GetString() : null;
                            var rqText = r.TryGetProperty("question", out var rqtProp) ? rqtProp.GetString()?.Trim() : null;
                            var corr = r.TryGetProperty("correct_answer", out var caProp) ? caProp.GetString() : null;

                            if (!string.IsNullOrEmpty(corr))
                            {
                                if (!string.IsNullOrEmpty(rqid)) answerBank[rqid] = corr;
                                if (!string.IsNullOrEmpty(rqText)) answerBank[rqText] = corr;
                            }
                        }
                    }

                    logger.LogInformation("[QUIZ-DOC] Tài liệu {DocId} lần {Attempt}: {Score}/{Total} (Passed: {Passed})", docId, attempt, lastScore, lastTotal, lastPassed);

                    if (lastScore >= 100)
                    {
                        return (true, 100m, lastTotal, true, $"Đạt 100/100 điểm tuyệt đối ở lần thử {attempt}.");
                    }

                    if (attempt == maxAttempts && (lastPassed || lastScore >= 80))
                    {
                        return (true, lastScore, lastTotal, true, $"Đạt {lastScore}/{lastTotal} điểm (Passed: True) ở lần thử {attempt}.");
                    }
                }

                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("[QUIZ-DOC] Timeout khi làm quiz tài liệu {DocId} lần {Attempt}.", docId, attempt);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[QUIZ-DOC] Lỗi khi làm quiz tài liệu {DocId} lần {Attempt}.", docId, attempt);
            }
        }

        return (lastPassed, lastScore, lastTotal, lastPassed, lastPassed ? $"Hoàn thành quiz: {lastScore}/{lastTotal}" : $"Quiz tài liệu đạt {lastScore}/{lastTotal} điểm.");
    }

    public async Task<(bool Success, decimal Score, int Total, bool Passed, bool CeuEarned, string Message)> SolveLessonQuizAsync(
        string courseId, string lessonId, string token, CancellationToken cancellationToken, int maxAttempts = 5)
    {
        // Kiểm tra trạng thái bài học trước
        try
        {
            using var stCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stCts.CancelAfter(TimeSpan.FromSeconds(15));
            using var stReq = Authorize(HttpMethod.Get, $"widget/courses/{courseId}/lessons/{lessonId}/quiz/status", token);
            using var stRes = await http.SendAsync(stReq, stCts.Token);
            if (stRes.IsSuccessStatusCode)
            {
                using var stDoc = await JsonDocument.ParseAsync(await stRes.Content.ReadAsStreamAsync(stCts.Token), cancellationToken: stCts.Token);
                if (stDoc.RootElement.TryGetProperty("data", out var stData))
                {
                    var ceuEarned = stData.TryGetProperty("ceu_earned", out var ceuP) && ceuP.GetBoolean();
                    decimal bestScore = 0;
                    if (stData.TryGetProperty("best_attempt", out var bestEl) && bestEl.ValueKind == JsonValueKind.Object)
                    {
                        if (bestEl.TryGetProperty("score", out var bsProp) && bsProp.TryGetDecimal(out var bsVal))
                            bestScore = bsVal;
                    }
                    if (ceuEarned && bestScore >= 100)
                    {
                        logger.LogInformation("[QUIZ-LESSON] Bài {LessonId} đã nhận đủ CEU với điểm tuyệt đối 100.", lessonId);
                        return (true, bestScore, 10, true, true, "Bài học đã nhận đủ CEU trước đó (100 điểm).");
                    }
                }
            }
        }
        catch { /* tiếp tục nếu không đọc được status */ }

        var answerBank = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        decimal lastScore = 0;
        int lastTotal = 0;
        bool lastPassed = false;
        bool lastCeu = false;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startCts.CancelAfter(TimeSpan.FromSeconds(20));

                using var startReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/lessons/{lessonId}/quiz/start", token);
                startReq.Content = JsonContent.Create(new { });
                using var startRes = await http.SendAsync(startReq, startCts.Token);
                if (!startRes.IsSuccessStatusCode)
                {
                    logger.LogWarning("[QUIZ-LESSON] Start quiz bài {LessonId} trả về HTTP {StatusCode}.", lessonId, (int)startRes.StatusCode);
                    if (attempt == 1)
                        return (false, 0, 0, false, false, $"Không thể bắt đầu quiz bài học (HTTP {(int)startRes.StatusCode}).");
                    break;
                }

                using var startDoc = await JsonDocument.ParseAsync(await startRes.Content.ReadAsStreamAsync(startCts.Token), cancellationToken: startCts.Token);
                if (!startDoc.RootElement.TryGetProperty("data", out var dataEl) ||
                    !dataEl.TryGetProperty("questions", out var questionsEl) ||
                    questionsEl.ValueKind != JsonValueKind.Array ||
                    questionsEl.GetArrayLength() == 0)
                {
                    return (false, 0, 0, false, false, "Bài học không có câu hỏi quiz.");
                }

                var submitAnswers = new List<object>();
                foreach (var q in questionsEl.EnumerateArray())
                {
                    var qid = q.TryGetProperty("id", out var qidProp) ? qidProp.GetString() : null;
                    if (string.IsNullOrEmpty(qid)) continue;

                    var qText = q.TryGetProperty("question", out var qtProp) ? qtProp.GetString()?.Trim() : null;

                    string? chosen = null;
                    if (answerBank.TryGetValue(qid, out var a1)) chosen = a1;
                    else if (!string.IsNullOrEmpty(qText) && answerBank.TryGetValue(qText, out var a2)) chosen = a2;

                    if (chosen == null)
                    {
                        if (q.TryGetProperty("options", out var optEl) && optEl.ValueKind == JsonValueKind.Array && optEl.GetArrayLength() > 0)
                            chosen = optEl[0].GetString();
                        else
                            chosen = "";
                    }

                    submitAnswers.Add(new { question_id = qid, answer = chosen ?? "" });
                }

                using var subCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                subCts.CancelAfter(TimeSpan.FromSeconds(25));

                using var subReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/lessons/{lessonId}/quiz/submit", token);
                subReq.Content = JsonContent.Create(new { answers = submitAnswers });

                using var subRes = await http.SendAsync(subReq, subCts.Token);
                if (!subRes.IsSuccessStatusCode)
                {
                    logger.LogWarning("[QUIZ-LESSON] Submit quiz bài {LessonId} lần {Attempt} trả về HTTP {StatusCode}.", lessonId, attempt, (int)subRes.StatusCode);
                    break;
                }

                using var subDoc = await JsonDocument.ParseAsync(await subRes.Content.ReadAsStreamAsync(subCts.Token), cancellationToken: subCts.Token);
                if (subDoc.RootElement.TryGetProperty("data", out var resData))
                {
                    lastScore = resData.TryGetProperty("score", out var scProp) && scProp.TryGetDecimal(out var sc) ? sc : 0m;
                    lastTotal = resData.TryGetProperty("total", out var totProp) && totProp.TryGetInt32(out var tot) ? tot : questionsEl.GetArrayLength();
                    lastPassed = resData.TryGetProperty("passed", out var passProp) && passProp.GetBoolean();
                    lastCeu = resData.TryGetProperty("ceu_earned", out var ceuProp) && ceuProp.GetBoolean();

                    if (resData.TryGetProperty("questions_review", out var revEl) && revEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in revEl.EnumerateArray())
                        {
                            var rqid = r.TryGetProperty("id", out var rqidProp) ? rqidProp.GetString() : null;
                            var rqText = r.TryGetProperty("question", out var rqtProp) ? rqtProp.GetString()?.Trim() : null;
                            var corr = r.TryGetProperty("correct_answer", out var caProp) ? caProp.GetString() : null;

                            if (!string.IsNullOrEmpty(corr))
                            {
                                if (!string.IsNullOrEmpty(rqid)) answerBank[rqid] = corr;
                                if (!string.IsNullOrEmpty(rqText)) answerBank[rqText] = corr;
                            }
                        }
                    }

                    logger.LogInformation("[QUIZ-LESSON] Bài {LessonId} lần {Attempt}: {Score}/{Total} (Passed: {Passed}, CEU: {Ceu})", lessonId, attempt, lastScore, lastTotal, lastPassed, lastCeu);

                    if (lastScore >= 100)
                    {
                        return (true, 100m, lastTotal, true, lastCeu, $"Đạt 100/100 điểm tuyệt đối ở lần thử {attempt}.");
                    }

                    if (attempt == maxAttempts && (lastPassed || lastScore >= 70))
                    {
                        return (true, lastScore, lastTotal, true, lastCeu, $"Đạt {lastScore}/{lastTotal} điểm (Passed: True) ở lần thử {attempt}.");
                    }
                }

                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("[QUIZ-LESSON] Timeout khi làm quiz bài {LessonId} lần {Attempt}.", lessonId, attempt);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[QUIZ-LESSON] Lỗi khi làm quiz bài {LessonId} lần {Attempt}.", lessonId, attempt);
            }
        }

        return (lastPassed, lastScore, lastTotal, lastPassed, lastCeu, lastPassed ? $"Hoàn thành quiz bài học: {lastScore}/{lastTotal}" : $"Quiz bài học đạt {lastScore}/{lastTotal} điểm.");
    }

    public async Task<(bool Success, decimal Score, int Total, bool Passed, string Message)> SolveExamQuizAsync(
        string courseId, string lessonId, JsonElement examConfig, string token, CancellationToken cancellationToken)
    {
        var answerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (examConfig.TryGetProperty("questions", out var questionsEl) && questionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in questionsEl.EnumerateArray())
            {
                var qid = q.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var ansLetter = q.TryGetProperty("answer", out var ansProp) ? ansProp.GetString()?.Trim() : null;
                if (string.IsNullOrEmpty(qid) || string.IsNullOrEmpty(ansLetter)) continue;

                string? chosenOpt = null;
                if (q.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var opt in optionsEl.EnumerateArray())
                    {
                        var optStr = opt.GetString();
                        if (optStr != null && optStr.TrimStart().StartsWith(ansLetter + ".", StringComparison.OrdinalIgnoreCase))
                        {
                            chosenOpt = optStr;
                            break;
                        }
                    }
                    if (chosenOpt == null && optionsEl.GetArrayLength() > 0)
                    {
                        chosenOpt = optionsEl[0].GetString();
                    }
                }
                if (chosenOpt != null) answerMap[qid] = chosenOpt;
            }
        }

        if (answerMap.Count == 0)
        {
            return (false, 0, 0, false, "Không tìm thấy đáp án trong cấu hình bài kiểm tra.");
        }

        using var startReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/lessons/{lessonId}/quiz/start", token);
        startReq.Content = JsonContent.Create(new { });
        using var startRes = await http.SendAsync(startReq, cancellationToken);
        if (!startRes.IsSuccessStatusCode)
        {
            return (false, 0, 0, false, "Không thể bắt đầu làm bài kiểm tra.");
        }

        using var startDoc = await JsonDocument.ParseAsync(await startRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var submitAnswers = new List<object>();
        if (startDoc.RootElement.TryGetProperty("data", out var dataEl) && dataEl.TryGetProperty("questions", out var activeQuestionsEl) && activeQuestionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var aq in activeQuestionsEl.EnumerateArray())
            {
                var aqid = aq.TryGetProperty("id", out var aqidProp) ? aqidProp.GetString() : null;
                if (string.IsNullOrEmpty(aqid)) continue;

                if (!answerMap.TryGetValue(aqid, out var chosen))
                {
                    if (aq.TryGetProperty("options", out var aqOptions) && aqOptions.ValueKind == JsonValueKind.Array && aqOptions.GetArrayLength() > 0)
                        chosen = aqOptions[0].GetString();
                    else
                        chosen = "";
                }
                submitAnswers.Add(new { question_id = aqid, answer = chosen ?? "" });
            }
        }

        using var subReq = Authorize(HttpMethod.Post, $"widget/courses/{courseId}/lessons/{lessonId}/quiz/submit", token);
        subReq.Content = JsonContent.Create(new { answers = submitAnswers });
        using var subRes = await http.SendAsync(subReq, cancellationToken);
        if (!subRes.IsSuccessStatusCode)
        {
            return (false, 0, 0, false, "Lỗi khi nộp bài kiểm tra.");
        }

        using var subDoc = await JsonDocument.ParseAsync(await subRes.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (subDoc.RootElement.TryGetProperty("data", out var resData))
        {
            var score = resData.TryGetProperty("score", out var scProp) && scProp.TryGetDecimal(out var sc) ? sc : 0m;
            var total = resData.TryGetProperty("total", out var totProp) && totProp.TryGetInt32(out var tot) ? tot : 0;
            var passed = resData.TryGetProperty("passed", out var passProp) && passProp.GetBoolean();
            return (true, score, total, passed, $"Nộp bài thành công: {score}/{total} điểm.");
        }

        return (true, 0, 0, true, "Đã nộp bài kiểm tra.");
    }

    public async Task<CourseHarvestResult> HarvestCourseLpAsync(string courseId, string token, CancellationToken cancellationToken)
    {
        var details = new List<string>();
        int docsCompleted = 0;
        int reflectionsSubmitted = 0;
        int postsPublished = 0;
        int quizzesSolved = 0;

        await SelectCourseAsync(courseId, token, cancellationToken);

        decimal? totalLpBefore = null;
        try
        {
            using var ovBeforeDoc = await GetCourseOverviewAsync(courseId, token, cancellationToken);
            if (ovBeforeDoc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("total_lp", out var lpB))
            {
                if (lpB.TryGetDecimal(out var lpVal)) totalLpBefore = lpVal;
            }
        }
        catch { }

        using var planDoc = await GetCourseLearningPlanAsync(courseId, token, cancellationToken);
        if (planDoc.RootElement.TryGetProperty("data", out var planData) && planData.TryGetProperty("semesters", out var semestersEl) && semestersEl.ValueKind == JsonValueKind.Array)
        {
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

                            // 1. Hoàn thành đọc tài liệu (LP1) & Giải Document Quiz nếu có
                            if (lesson.TryGetProperty("docs_info", out var docsInfoEl) && docsInfoEl.ValueKind == JsonValueKind.Array && docsInfoEl.GetArrayLength() > 0)
                            {
                                foreach (var d in docsInfoEl.EnumerateArray())
                                {
                                    var did = d.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                    var dTitle = d.TryGetProperty("title", out var tProp) ? tProp.GetString() : null;
                                    var hasQuiz = d.TryGetProperty("has_quiz", out var hqProp) && hqProp.GetBoolean();
                                    if (string.IsNullOrEmpty(did)) continue;
                                    docIds.Add(did);

                                    var ok = await CompleteDocReadingAsync(courseId, lessonId, did, dTitle, token, cancellationToken);
                                    if (ok) docsCompleted++;
                                    await Task.Delay(150, cancellationToken);

                                    if (hasQuiz)
                                    {
                                        var docQuizRes = await SolveDocumentQuizAsync(courseId, lessonId, did, token, cancellationToken);
                                        if (docQuizRes.Success)
                                        {
                                            quizzesSolved++;
                                            details.Add($"Tuần {weekNumber} - {lessonTitle}: Đã giải Quiz tài liệu {dTitle ?? did} ({docQuizRes.Score}/{docQuizRes.Total}đ)");
                                        }
                                        await Task.Delay(200, cancellationToken);
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
                                    var ok = await CompleteDocReadingAsync(courseId, lessonId, did, null, token, cancellationToken);
                                    if (ok) docsCompleted++;
                                    await Task.Delay(150, cancellationToken);
                                }
                            }

                            // 2. Đăng bài thảo luận (LP5 - 3 CEU)
                            if (docIds.Count > 0)
                            {
                                var postOk = await PostDiscussionAsync(courseId, lessonId, docIds, token, cancellationToken);
                                if (postOk)
                                {
                                    postsPublished++;
                                    details.Add($"Tuần {weekNumber} - {lessonTitle}: Đã đăng bài thảo luận (+3 LP)");
                                }
                                await Task.Delay(150, cancellationToken);
                            }

                            // 3. Nộp nhật ký học tập (LP4 - 8 CEU)
                            var refRes = await SubmitLp4ReflectionAsync(courseId, lessonId, weekNumber, token, cancellationToken);
                            if (refRes.Success && !refRes.AlreadySubmitted)
                            {
                                reflectionsSubmitted++;
                                details.Add($"Tuần {weekNumber} - {lessonTitle}: Đã nộp nhật ký phản xạ (+8 LP)");
                            }
                            await Task.Delay(150, cancellationToken);

                            // 4. Giải Quiz bài học (tab "Quiz" của bài học)
                            var lessonQuizRes = await SolveLessonQuizAsync(courseId, lessonId, token, cancellationToken);
                            if (lessonQuizRes.Success)
                            {
                                quizzesSolved++;
                                details.Add($"Tuần {weekNumber} - {lessonTitle}: Đã giải Quiz bài học ({lessonQuizRes.Score}/{lessonQuizRes.Total}đ)");
                            }
                            await Task.Delay(200, cancellationToken);
                        }
                        else if (lessonType == "exam")
                        {
                            // 5. Tự động giải bài kiểm tra tuần (LP2 Exam)
                            if (lesson.TryGetProperty("exam_config", out var examCfg) && examCfg.ValueKind == JsonValueKind.Object)
                            {
                                var quizRes = await SolveExamQuizAsync(courseId, lessonId, examCfg, token, cancellationToken);
                                if (quizRes.Success)
                                {
                                    quizzesSolved++;
                                    details.Add($"Tuần {weekNumber} - {lessonTitle}: Đã giải bài kiểm tra ({quizRes.Score}/{quizRes.Total} điểm)");
                                }
                                else
                                {
                                    details.Add($"Tuần {weekNumber} - {lessonTitle}: {quizRes.Message}");
                                }
                                await Task.Delay(300, cancellationToken);
                            }
                        }
                    }
                }
            }
        }

        decimal? totalLpAfter = null;
        decimal? totalCeuAfter = null;
        try
        {
            using var ovAfterDoc = await GetCourseOverviewAsync(courseId, token, cancellationToken);
            if (ovAfterDoc.RootElement.TryGetProperty("data", out var dAfter))
            {
                if (dAfter.TryGetProperty("total_lp", out var lpA) && lpA.TryGetDecimal(out var lpVal)) totalLpAfter = lpVal;
                if (dAfter.TryGetProperty("total_ceu", out var ceuA) && ceuA.TryGetDecimal(out var ceuVal)) totalCeuAfter = ceuVal;
            }
        }
        catch { }

        return new CourseHarvestResult(
            courseId,
            docsCompleted,
            reflectionsSubmitted,
            postsPublished,
            quizzesSolved,
            details,
            totalLpBefore,
            totalLpAfter,
            totalCeuAfter
        );
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var request = Authorize(HttpMethod.Get, path, token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<RemoteAttendanceResult> AttendAsync(string redirectToken, string token, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = Authorize(HttpMethod.Post, $"widget/live-sessions/{redirectToken}/attend", token);
        request.Content = JsonContent.Create(new { });
        using var response = await http.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var root = document.RootElement;
            var payload = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;
            var body = payload.Deserialize<RemoteAttendanceResult>(Json);
            if (!response.IsSuccessStatusCode)
                return new RemoteAttendanceResult(false, false, "rejected", GetRemoteMessage(root) ?? $"Eduso từ chối yêu cầu (HTTP {(int)response.StatusCode}).", (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
            return body is null
                ? new RemoteAttendanceResult(false, false, "unknown", "Phản hồi điểm danh không hợp lệ.", (int)response.StatusCode, stopwatch.ElapsedMilliseconds)
                : body with { HttpStatusCode = (int)response.StatusCode, DurationMs = stopwatch.ElapsedMilliseconds };
        }
        catch (JsonException)
        {
            return response.IsSuccessStatusCode
                ? new RemoteAttendanceResult(false, false, "unknown", "Eduso trả về phản hồi không đọc được.", (int)response.StatusCode, stopwatch.ElapsedMilliseconds)
                : new RemoteAttendanceResult(false, false, "rejected", $"Eduso từ chối yêu cầu (HTTP {(int)response.StatusCode}).", (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
        }
    }

    private static string? GetRemoteMessage(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "message", "detail", "error" })
            if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static HttpRequestMessage Authorize(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "[redacted]";
        return $"{email[0]}***{email[at..]}";
    }
}

public sealed class EdusoApiException(
    string message,
    HttpStatusCode? statusCode,
    string? rawResponse,
    string curlCommand,
    Exception? innerException = null)
    : HttpRequestException(message, innerException, statusCode)
{
    public string? RawResponse { get; } = rawResponse;
    public string CurlCommand { get; } = curlCommand;
}

public sealed record WidgetSession([property: JsonPropertyName("session_token")] string? SessionToken);
public sealed record CourseEnvelope(bool Success, CourseData? Data);
public sealed record CourseData(IReadOnlyList<RemoteCourse> Courses);
public sealed record LiveEnvelope(bool Success, IReadOnlyList<RemoteLiveSession>? Data);
public sealed record RemoteCourse(
    [property: JsonPropertyName("course_id")] string CourseId, string Code, string Name, string? Color,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("current_lesson")] string? CurrentLesson);
public sealed record RemoteLiveSession(
    string Id, string Title,
    [property: JsonPropertyName("scheduled_at")] DateTimeOffset ScheduledAt,
    [property: JsonPropertyName("duration_minutes")] int DurationMinutes,
    [property: JsonPropertyName("redirect_token")] string RedirectToken,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("is_ended")] bool IsEnded,
    bool Attended);
public sealed record RemoteAttendanceResult(bool Valid,
    [property: JsonPropertyName("already_recorded")] bool AlreadyRecorded,
    string? Status, string? Message, int? HttpStatusCode = null, long DurationMs = 0);
public sealed record CourseHarvestResult(
    string CourseId,
    int DocsCompleted,
    int ReflectionsSubmitted,
    int PostsPublished,
    int QuizzesSolved,
    IReadOnlyList<string> Details,
    decimal? TotalLpBefore,
    decimal? TotalLpAfter,
    decimal? TotalCeuAfter);
