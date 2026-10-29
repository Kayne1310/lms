using Hangfire;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Services;

public sealed class AttendanceSyncService(
    AttendanceDbContext db,
    EdusoWidgetClient eduso,
    EdusoAccountSessionService accountSessions,
    IBackgroundJobClient jobs,
    ILogger<AttendanceSyncService> logger)
{
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    [DisableConcurrentExecution("eduso-attendance-sync", 3600)]
    public async Task SyncAllAccountsAsync(CancellationToken cancellationToken)
    {
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            var accountIds = await db.StudentAccounts.Select(x => x.Id).ToListAsync(cancellationToken);
            foreach (var accountId in accountIds)
                await SyncAccountCoreAsync(accountId, cancellationToken);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    [DisableConcurrentExecution("eduso-attendance-sync", 3600)]
    public async Task SyncAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await SyncGate.WaitAsync(cancellationToken);
        try
        {
            await SyncAccountCoreAsync(accountId, cancellationToken);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    private async Task SyncAccountCoreAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var accountCredentials = await db.StudentAccounts.AsNoTracking()
            .Where(x => x.Id == accountId)
            .Select(x => new { x.Email, x.EdusoPasswordProtected })
            .SingleOrDefaultAsync(cancellationToken);
        if (accountCredentials is null || string.IsNullOrWhiteSpace(accountCredentials.EdusoPasswordProtected)) return;

        string token;
        try
        {
            token = await accountSessions.CreateSessionAsync(accountCredentials.Email, accountCredentials.EdusoPasswordProtected, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bỏ qua đồng bộ cho tài khoản {Email} do lỗi xác thực mật khẩu từ DB.", accountCredentials.Email);
            return;
        }
        var remoteCourses = await eduso.GetCoursesAsync(token, cancellationToken);
        var remoteData = new List<(RemoteCourse Course, IReadOnlyList<RemoteLiveSession> Sessions)>();

        foreach (var remoteCourse in remoteCourses)
        {
            var sessions = await eduso.GetLiveSessionsAsync(remoteCourse.CourseId, token, cancellationToken);
            remoteData.Add((remoteCourse, sessions));
        }

        // Fetch first, then track a fresh graph so slow remote calls cannot leave stale rows in memory.
        db.ChangeTracker.Clear();
        var account = await db.StudentAccounts
            .Include(x => x.Courses)
            .ThenInclude(x => x.LiveSessions)
            .SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null) return;

        foreach (var (remoteCourse, remoteSessions) in remoteData)
        {
            var course = account.Courses.SingleOrDefault(x => x.RemoteCourseId == remoteCourse.CourseId);
            if (course is null)
            {
                course = new Course
                {
                    StudentAccountId = account.Id,
                    RemoteCourseId = remoteCourse.CourseId,
                    Code = remoteCourse.Code,
                    Name = remoteCourse.Name,
                };
                db.Courses.Add(course);
                account.Courses.Add(course);
            }

            course.Code = remoteCourse.Code;
            course.Name = remoteCourse.Name;
            course.Color = remoteCourse.Color;
            course.CurrentLesson = remoteCourse.CurrentLesson;
            course.DocumentCount = remoteCourse.DocumentCount;

            foreach (var remote in remoteSessions)
            {
                var session = course.LiveSessions.SingleOrDefault(x => x.RemoteSessionId == remote.Id);
                if (session is null)
                {
                    session = new LiveSession
                    {
                        Course = course,
                        RemoteSessionId = remote.Id,
                        RedirectToken = remote.RedirectToken,
                        Title = remote.Title,
                    };
                    db.LiveSessions.Add(session);
                    course.LiveSessions.Add(session);
                }

                session.RedirectToken = remote.RedirectToken;
                session.Title = remote.Title;
                session.ScheduledAtUtc = remote.ScheduledAt;
                session.DurationMinutes = remote.DurationMinutes;
                session.IsActive = remote.IsActive;
                session.IsEnded = remote.IsEnded;
                session.RemoteAttended = remote.Attended;
                session.LastSyncedAtUtc = DateTimeOffset.UtcNow;
            }
        }

        account.LastSyncedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            var conflicts = string.Join(", ", exception.Entries.Select(entry =>
            {
                var key = entry.Metadata.FindPrimaryKey();
                var identity = key is null
                    ? "unknown"
                    : string.Join(",", key.Properties.Select(property => $"{property.Name}={entry.Property(property.Name).CurrentValue}"));
                return $"{entry.Metadata.ClrType.Name}[{identity}]";
            }));
            logger.LogError(exception, "Attendance sync lost database rows for {Conflicts}.", conflicts);
            throw;
        }

        foreach (var session in account.Courses.SelectMany(x => x.LiveSessions)
                     .Where(x => x.HangfireJobId is null && !x.IsEnded && !x.RemoteAttended && !x.IsManualJob
                         && account.AutoAttendanceEnabled && x.Course.AutoAttendanceEnabled))
        {
            var runAt = session.ScheduledAtUtc.AddMinutes(-account.AttendanceLeadTimeMinutes);
            if (runAt <= DateTimeOffset.UtcNow)
                runAt = DateTimeOffset.UtcNow.AddSeconds(5);
            session.HangfireJobId = jobs.Schedule<AttendanceWorker>(
                worker => worker.AttendAsync(session.Id, CancellationToken.None), runAt);
            session.JobScheduledAtUtc = runAt;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
