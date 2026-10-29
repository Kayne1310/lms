using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Services;

namespace UtcAttendance.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/dashboard")]
public sealed class DashboardController(AttendanceDbContext db, IBackgroundJobClient jobs) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> Get(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var account = await db.StudentAccounts.AsNoTracking().Where(x => x.Id == accountId.Value)
            .Select(x => new { x.Id, x.Email, x.AutoAttendanceEnabled, x.AttendanceLeadTimeMinutes, x.LastSyncedAtUtc })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null) return NotFound(new { message = "Không tìm thấy tài khoản." });

        var courses = await db.Courses.AsNoTracking().Where(x => x.StudentAccountId == account.Id)
            .OrderBy(x => x.Code).Select(course => new
            {
                course.Id, course.Code, course.Name, course.Color, course.CurrentLesson, course.DocumentCount, course.AutoAttendanceEnabled,
                sessions = course.LiveSessions.OrderBy(x => x.ScheduledAtUtc).Select(session => new
                {
                    session.Id, session.Title, session.ScheduledAtUtc, session.DurationMinutes, session.IsActive, session.IsEnded,
                    session.RemoteAttended, session.IsManualJob, isQueued = session.HangfireJobId != null, session.JobScheduledAtUtc,
                    lastAttempt = session.AttendanceAttempts.OrderByDescending(x => x.CreatedAtUtc)
                        .Select(x => new { x.Status, x.Message, x.CreatedAtUtc, x.DetailsJson }).FirstOrDefault()
                }).ToList()
            }).ToListAsync(cancellationToken);
        var history = await db.AttendanceAttempts.AsNoTracking()
            .Where(attempt => attempt.LiveSession.Course.StudentAccountId == account.Id)
            .OrderByDescending(x => x.CreatedAtUtc).Take(100)
            .Select(attempt => new
            {
                attempt.Id, attempt.Status, attempt.Message, attempt.CreatedAtUtc, attempt.DetailsJson,
                sessionTitle = attempt.LiveSession.Title, courseName = attempt.LiveSession.Course.Name
            }).ToListAsync(cancellationToken);

        return Ok(new { account, courses, history });
    }

    [HttpPost("logout")]
    public async Task<ActionResult> Logout(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();

        var account = await db.StudentAccounts.SingleOrDefaultAsync(x => x.Id == accountId.Value, cancellationToken);
        if (account is null) return NoContent();
        account.ActiveSessionId = null;
        account.IsConnected = false;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("sync")]
    public async Task<ActionResult> Sync(CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        if (!await db.StudentAccounts.AnyAsync(x => x.Id == accountId.Value, cancellationToken)) return NotFound();

        var jobId = jobs.Enqueue<AttendanceSyncService>(service => service.SyncAccountAsync(accountId.Value, CancellationToken.None));
        return Accepted(new { jobId });
    }

    [HttpPost("sessions/{sessionId:guid}/attend")]
    public async Task<ActionResult> AttendManually(Guid sessionId, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var session = await db.LiveSessions.Include(x => x.Course)
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.Course.StudentAccountId == accountId.Value, cancellationToken);
        if (session is null) return NotFound(new { message = "Không tìm thấy buổi học." });
        if (session.HangfireJobId is not null)
            return Conflict(new { message = "Buổi học đã có tác vụ trong hàng đợi." });

        session.HangfireJobId = jobs.Schedule<AttendanceWorker>(worker =>
            worker.AttendManuallyAsync(session.Id, CancellationToken.None), TimeSpan.FromSeconds(1));
        session.IsManualJob = true;
        session.JobScheduledAtUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        await db.SaveChangesAsync(cancellationToken);
        return Accepted(new { jobId = session.HangfireJobId });
    }

    [HttpPatch("automation/schedule")]
    public async Task<ActionResult> SetAttendanceSchedule([FromBody] AttendanceScheduleRequest request, CancellationToken cancellationToken)
    {
        if (request.LeadTimeMinutes is < 0 or > 60)
            return BadRequest(new { message = "Thời gian phải từ 0 đến 60 phút." });

        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var account = await db.StudentAccounts.Where(x => x.Id == accountId.Value).Include(x => x.Courses).ThenInclude(x => x.LiveSessions)
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null) return NotFound();

        account.AttendanceLeadTimeMinutes = request.LeadTimeMinutes;
        foreach (var course in account.Courses)
        foreach (var session in course.LiveSessions)
        {
            if (session.IsManualJob) continue;
            if (session.HangfireJobId is not null) jobs.Delete(session.HangfireJobId);
            session.HangfireJobId = null;
            session.JobScheduledAtUtc = null;
            if (!account.AutoAttendanceEnabled || !course.AutoAttendanceEnabled || session.IsEnded || session.RemoteAttended)
                continue;

            var runAt = session.ScheduledAtUtc.AddMinutes(-request.LeadTimeMinutes);
            if (runAt <= DateTimeOffset.UtcNow) runAt = DateTimeOffset.UtcNow.AddSeconds(5);
            session.HangfireJobId = jobs.Schedule<AttendanceWorker>(worker => worker.AttendAsync(session.Id, CancellationToken.None), runAt);
            session.JobScheduledAtUtc = runAt;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { account.AttendanceLeadTimeMinutes });
    }

    [HttpPatch("sessions/{sessionId:guid}/schedule")]
    public async Task<ActionResult> SetSessionSchedule(Guid sessionId, [FromBody] SessionScheduleRequest request, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var session = await db.LiveSessions.Include(x => x.Course).ThenInclude(x => x.StudentAccount)
            .SingleOrDefaultAsync(x => x.Id == sessionId && x.Course.StudentAccountId == accountId.Value, cancellationToken);
        if (session is null) return NotFound();
        if (session.IsManualJob) return Conflict(new { message = "Không thể sửa giờ của yêu cầu điểm danh thủ công đang chạy." });
        if (session.IsEnded || session.RemoteAttended) return Conflict(new { message = "Buổi học đã kết thúc hoặc đã được ghi nhận điểm danh." });
        if (!session.Course.StudentAccount.AutoAttendanceEnabled || !session.Course.AutoAttendanceEnabled)
            return Conflict(new { message = "Hãy bật tự động điểm danh cho tài khoản và môn học trước." });

        var runAt = request.JobScheduledAtUtc.ToUniversalTime();
        var now = DateTimeOffset.UtcNow;
        if (runAt <= now || runAt > session.ScheduledAtUtc.AddMinutes(session.DurationMinutes))
            return BadRequest(new { message = "Giờ chạy phải ở tương lai và không muộn hơn giờ kết thúc buổi học." });

        if (session.HangfireJobId is not null) jobs.Delete(session.HangfireJobId);
        session.HangfireJobId = jobs.Schedule<AttendanceWorker>(worker => worker.AttendAsync(session.Id, CancellationToken.None), runAt);
        session.JobScheduledAtUtc = runAt;
        session.IsManualJob = false;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { session.Id, session.JobScheduledAtUtc });
    }

    [HttpPatch("automation")]
    public async Task<ActionResult> SetGlobalAutomation([FromBody] AutomationRequest request, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var account = await db.StudentAccounts.SingleOrDefaultAsync(x => x.Id == accountId.Value, cancellationToken);
        if (account is null) return NotFound();
        account.AutoAttendanceEnabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPatch("courses/{courseId:guid}/automation")]
    public async Task<ActionResult> SetCourseAutomation(Guid courseId, [FromBody] AutomationRequest request, CancellationToken cancellationToken)
    {
        var accountId = CurrentAccountId();
        if (accountId is null) return Unauthorized();
        var course = await db.Courses.SingleOrDefaultAsync(
            x => x.Id == courseId && x.StudentAccountId == accountId.Value, cancellationToken);
        if (course is null) return NotFound();
        course.AutoAttendanceEnabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private Guid? CurrentAccountId()
        => Guid.TryParse(User.FindFirst("sub")?.Value, out var accountId) ? accountId : null;
}

public sealed record AutomationRequest(bool Enabled);
public sealed record AttendanceScheduleRequest(int LeadTimeMinutes);
public sealed record SessionScheduleRequest(DateTimeOffset JobScheduledAtUtc);
