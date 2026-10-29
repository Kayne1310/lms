using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Data;

public sealed class AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : DbContext(options)
{
    public DbSet<StudentAccount> StudentAccounts => Set<StudentAccount>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<AttendanceAttempt> AttendanceAttempts => Set<AttendanceAttempt>();
    public DbSet<LearningProgressCheck> LearningProgressChecks => Set<LearningProgressCheck>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StudentAccount>().HasIndex(x => x.Email).IsUnique();
        modelBuilder.Entity<Course>().HasIndex(x => new { x.StudentAccountId, x.RemoteCourseId }).IsUnique();
        modelBuilder.Entity<LiveSession>().HasIndex(x => new { x.CourseId, x.RemoteSessionId }).IsUnique();
        modelBuilder.Entity<AttendanceAttempt>().HasIndex(x => new { x.LiveSessionId, x.CreatedAtUtc });
        modelBuilder.Entity<Course>().Property(x => x.Color).HasMaxLength(16);
        modelBuilder.Entity<AttendanceAttempt>().Property(x => x.Message).HasMaxLength(500);
        modelBuilder.Entity<AttendanceAttempt>().Property(x => x.DetailsJson).HasColumnType("text");
        modelBuilder.Entity<LearningProgressCheck>().HasIndex(x => new { x.StudentAccountId, x.CreatedAtUtc });
        modelBuilder.Entity<LearningProgressCheck>().Property(x => x.Status).HasMaxLength(30);
        modelBuilder.Entity<LearningProgressCheck>().Property(x => x.Message).HasMaxLength(500);
        modelBuilder.Entity<LearningProgressCheck>().Property(x => x.LpByTypeJson).HasColumnType("text");
        modelBuilder.Entity<LearningProgressCheck>().Property(x => x.LessonsJson).HasColumnType("text");
    }
}
