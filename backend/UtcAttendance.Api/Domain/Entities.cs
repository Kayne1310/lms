namespace UtcAttendance.Api.Domain;

public sealed class StudentAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public string? EdusoPasswordProtected { get; set; }
    public bool IsConnected { get; set; } = true;
    public Guid? ActiveSessionId { get; set; }
    public bool AutoAttendanceEnabled { get; set; } = true;
    public int AttendanceLeadTimeMinutes { get; set; } = 5;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public List<Course> Courses { get; set; } = [];
}

public sealed class Course
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentAccountId { get; set; }
    public StudentAccount StudentAccount { get; set; } = null!;
    public required string RemoteCourseId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Color { get; set; }
    public string? CurrentLesson { get; set; }
    public int DocumentCount { get; set; }
    public bool AutoAttendanceEnabled { get; set; } = true;
    public List<LiveSession> LiveSessions { get; set; } = [];
}

public sealed class LiveSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public required string RemoteSessionId { get; set; }
    public required string RedirectToken { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset ScheduledAtUtc { get; set; }
    public int DurationMinutes { get; set; }
    public bool IsActive { get; set; }
    public bool IsEnded { get; set; }
    public bool RemoteAttended { get; set; }
    public string? HangfireJobId { get; set; }
    public DateTimeOffset? JobScheduledAtUtc { get; set; }
    public bool IsManualJob { get; set; }
    public DateTimeOffset LastSyncedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<AttendanceAttempt> AttendanceAttempts { get; set; } = [];
}

public sealed class AttendanceAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LiveSessionId { get; set; }
    public LiveSession LiveSession { get; set; } = null!;
    public required string Status { get; set; }
    public required string Message { get; set; }
    public string? DetailsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LearningProgressCheck
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StudentAccountId { get; set; }
    public Guid CourseId { get; set; }
    public required string CourseCode { get; set; }
    public required string CourseName { get; set; }
    public required string Status { get; set; }
    public string? Message { get; set; }
    public decimal? TotalLp { get; set; }
    public decimal? DeltaLp { get; set; }
    public decimal? TotalCeu { get; set; }
    public decimal? ProgressPct { get; set; }
    public string? LpByTypeJson { get; set; }
    public string? LessonsJson { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
