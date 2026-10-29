using Hangfire;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Diagnostics;
using System.Text.Json;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Services;

public sealed class AttendanceWorker(AttendanceDbContext db, EdusoWidgetClient eduso, EdusoAccountSessionService accountSessions, IBackgroundJobClient jobs)
{
    [AutomaticRetry(Attempts = 2)]
    public async Task AttendAsync(Guid liveSessionId, CancellationToken cancellationToken)
        => await AttendCoreAsync(liveSessionId, allowManual: false, cancellationToken);

    [AutomaticRetry(Attempts = 2)]
    public async Task AttendManuallyAsync(Guid liveSessionId, CancellationToken cancellationToken)
        => await AttendCoreAsync(liveSessionId, allowManual: true, cancellationToken);

    private async Task AttendCoreAsync(Guid liveSessionId, bool allowManual, CancellationToken cancellationToken)
    {
        var session = await db.LiveSessions.Include(x => x.Course).ThenInclude(x => x.StudentAccount)
            .Include(x => x.AttendanceAttempts).SingleOrDefaultAsync(x => x.Id == liveSessionId, cancellationToken);
        if (session is null) return;
        if (!allowManual && (!session.Course.StudentAccount.AutoAttendanceEnabled || !session.Course.AutoAttendanceEnabled))
        {
            await LogAsync(session, "disabled", "Tự động điểm danh đang tắt.", cancellationToken); return;
        }
        if (!allowManual && (session.RemoteAttended || session.AttendanceAttempts.Any(x => x.Status is "success" or "already_recorded")))
        {
            return;
        }

        var total = Stopwatch.StartNew();
        var tokenDurationMs = 0L;
        var liveCheckDurationMs = 0L;
        var attendDurationMs = 0L;
        var tokenRefreshAttempted = false;
        Stopwatch? liveCheckWatch = null;
        var currentStep = "create_session";

        async Task<string> CreateFreshTokenAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                return await accountSessions.CreateSessionAsync(session.Course.StudentAccount, cancellationToken);
            }
            finally
            {
                tokenDurationMs += stopwatch.ElapsedMilliseconds;
            }
        }

        try
        {
            var token = await CreateFreshTokenAsync();
            if (!allowManual)
            {
                currentStep = "live_sessions";
                liveCheckWatch = Stopwatch.StartNew();
                IReadOnlyList<RemoteLiveSession> liveSessions;
                try
                {
                    liveSessions = await eduso.GetLiveSessionsAsync(session.Course.RemoteCourseId, token, cancellationToken);
                }
                catch (HttpRequestException exception) when (IsExpiredSession(exception))
                {
                    tokenRefreshAttempted = true;
                    currentStep = "create_session";
                    token = await CreateFreshTokenAsync();
                    currentStep = "live_sessions";
                    liveSessions = await eduso.GetLiveSessionsAsync(session.Course.RemoteCourseId, token, cancellationToken);
                }
                liveCheckDurationMs = liveCheckWatch.ElapsedMilliseconds;
                var current = liveSessions.SingleOrDefault(x => x.Id == session.RemoteSessionId);
                if (current is null)
                {
                    await LogAsync(session, "not_found", "Không tìm thấy buổi trên Eduso.", cancellationToken);
                    return;
                }

                session.IsActive = current.IsActive;
                session.IsEnded = current.IsEnded;
                session.RemoteAttended = current.Attended;
                session.ScheduledAtUtc = current.ScheduledAt;
                session.LastSyncedAtUtc = DateTimeOffset.UtcNow;

                if (current.Attended)
                {
                    await LogAsync(session, "already_recorded", "Eduso xác nhận buổi học đã được điểm danh.", cancellationToken);
                    return;
                }

                if (current.IsEnded)
                {
                    await LogAsync(session, "not_live", "Buổi đã kết thúc; job tự động không gửi request.", cancellationToken);
                    return;
                }

                if (!current.IsActive)
                {
                    var windowEnd = session.ScheduledAtUtc.AddMinutes(session.DurationMinutes);
                    if (DateTimeOffset.UtcNow < windowEnd)
                    {
                        var now = DateTimeOffset.UtcNow;
                        var nextCheck = now < session.ScheduledAtUtc ? session.ScheduledAtUtc : now.AddSeconds(30);
                        session.HangfireJobId = jobs.Schedule<AttendanceWorker>(
                            worker => worker.AttendAsync(session.Id, CancellationToken.None), nextCheck);
                        session.JobScheduledAtUtc = nextCheck;
                        session.IsManualJob = false;
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    }

                    await LogAsync(session, "not_live", "Buổi chưa live trong khung giờ đã lên lịch; job kết thúc.", cancellationToken);
                    return;
                }
            }

            // A manual click is an explicit request to test Eduso's attend endpoint.
            // Automated jobs reach this point only after Eduso reports the session active.
            currentStep = "attend";
            var attendWatch = Stopwatch.StartNew();
            var result = await eduso.AttendAsync(session.RedirectToken, token, cancellationToken);
            if (result.HttpStatusCode is 401 or 403)
            {
                tokenRefreshAttempted = true;
                currentStep = "create_session";
                token = await CreateFreshTokenAsync();
                currentStep = "attend";
                result = await eduso.AttendAsync(session.RedirectToken, token, cancellationToken);
            }
            attendDurationMs = attendWatch.ElapsedMilliseconds;
            var status = result.AlreadyRecorded ? "already_recorded" : result.Valid ? "success" : result.Status ?? "rejected";
            var message = result.AlreadyRecorded ? "Hệ thống Eduso xác nhận đã điểm danh trước đó." : result.Message ?? "Eduso không trả về chi tiết.";
            if (allowManual && (result.Valid || result.AlreadyRecorded)) session.RemoteAttended = true;
            var details = JsonSerializer.Serialize(new
            {
                request = new
                {
                    method = "POST",
                    path = "/api/v1/widget/live-sessions/{redirectToken}/attend",
                    authorization = "Bearer [redacted]",
                    body = new { }
                },
                response = new { result.HttpStatusCode, result.Valid, result.AlreadyRecorded, result.Status, result.Message, tokenRefreshAttempted },
                timingMs = new { createSession = tokenDurationMs, liveSessionCheck = liveCheckDurationMs, attend = attendDurationMs, total = total.ElapsedMilliseconds }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await LogAsync(session, status, message, cancellationToken, details);

            if (!allowManual && status == "before")
            {
                session.JobScheduledAtUtc = session.ScheduledAtUtc > DateTimeOffset.UtcNow
                    ? session.ScheduledAtUtc
                    : DateTimeOffset.UtcNow.AddSeconds(5);
                session.HangfireJobId = jobs.Schedule<AttendanceWorker>(worker => worker.AttendAsync(session.Id, CancellationToken.None), session.JobScheduledAtUtc.Value);
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (HttpRequestException exception)
        {
            if (currentStep == "live_sessions" && liveCheckWatch is not null)
                liveCheckDurationMs = liveCheckWatch.ElapsedMilliseconds;
            else if (currentStep == "attend")
                attendDurationMs = total.ElapsedMilliseconds - tokenDurationMs - liveCheckDurationMs;

            var httpStatusCode = exception.StatusCode is null ? (int?)null : (int)exception.StatusCode.Value;
            var responseStatus = httpStatusCode switch
            {
                null => "network_error",
                >= 400 => "http_error",
                _ => "protocol_error"
            };
            var safeMessage = exception.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when currentStep == "create_session"
                    => "Eduso từ chối xác thực. Hãy đăng nhập lại bằng mật khẩu Eduso.",
                not null when httpStatusCode >= 400 => $"Eduso trả HTTP {httpStatusCode} ở bước {currentStep}.",
                not null => $"Phản hồi Eduso không hợp lệ (HTTP {httpStatusCode}) ở bước {currentStep}.",
                _ => $"Không kết nối được Eduso ở bước {currentStep}."
            };
            if (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && currentStep == "create_session")
                session.Course.StudentAccount.IsConnected = false;

            var edusoException = exception as EdusoApiException;
            var details = JsonSerializer.Serialize(new
            {
                curl = edusoException?.CurlCommand,
                edusoResponse = edusoException?.RawResponse,
                httpStatusCode,
                stage = currentStep,
                tokenRefreshAttempted,
                exceptionType = exception.GetType().Name,
                networkDetail = exception.StatusCode is null ? exception.InnerException?.Message ?? exception.Message : null,
                timingMs = new { createSession = tokenDurationMs, liveSessionCheck = liveCheckDurationMs, attend = attendDurationMs, total = total.ElapsedMilliseconds }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await LogAsync(session, responseStatus == "http_error" ? "rejected" : "error", safeMessage, cancellationToken, details);
            if (!allowManual && IsTransient(exception)) await ScheduleAutoRetryAsync(session, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var details = JsonSerializer.Serialize(new
            {
                stage = currentStep,
                curlTemplate = currentStep == "create_session" ? EdusoWidgetClient.CreateSessionCurlTemplate() : null,
                errorType = exception.GetType().Name,
                error = exception.Message,
                timingMs = new { createSession = tokenDurationMs, liveSessionCheck = liveCheckDurationMs, attend = attendDurationMs, total = total.ElapsedMilliseconds }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await LogAsync(session, "error", exception.Message, cancellationToken, details);
        }
    }

    private async Task LogAsync(LiveSession session, string status, string message, CancellationToken cancellationToken, string? detailsJson = null)
    {
        session.HangfireJobId = null;
        session.JobScheduledAtUtc = null;
        session.IsManualJob = false;
        db.AttendanceAttempts.Add(new AttendanceAttempt
        {
            LiveSessionId = session.Id,
            Status = status,
            Message = message,
            DetailsJson = detailsJson
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ScheduleAutoRetryAsync(LiveSession session, CancellationToken cancellationToken)
    {
        if (session.IsEnded || session.RemoteAttended || !session.Course.StudentAccount.AutoAttendanceEnabled || !session.Course.AutoAttendanceEnabled)
            return;

        var now = DateTimeOffset.UtcNow;
        var retryAt = now < session.ScheduledAtUtc ? session.ScheduledAtUtc : now.AddSeconds(30);
        if (retryAt >= session.ScheduledAtUtc.AddMinutes(session.DurationMinutes)) return;

        session.HangfireJobId = jobs.Schedule<AttendanceWorker>(worker => worker.AttendAsync(session.Id, CancellationToken.None), retryAt);
        session.JobScheduledAtUtc = retryAt;
        session.IsManualJob = false;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsTransient(HttpRequestException exception)
        => exception.StatusCode is null
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            || (int?)exception.StatusCode >= 500;

    private static bool IsExpiredSession(HttpRequestException exception)
        => exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
