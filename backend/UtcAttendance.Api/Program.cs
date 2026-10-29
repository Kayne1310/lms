using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;
using UtcAttendance.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var keyRingPath = builder.Configuration["DataProtection:KeysPath"];
if (string.IsNullOrWhiteSpace(keyRingPath))
{
    var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    keyRingPath = Path.Combine(string.IsNullOrWhiteSpace(userProfile) ? builder.Environment.ContentRootPath : userProfile,
        ".aspnet", "DataProtection-Keys", "UtcAttendance");
}
Directory.CreateDirectory(keyRingPath);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set ConnectionStrings:Postgres in appsettings.json before starting the API.");

builder.Services.Configure<EdusoOptions>(builder.Configuration.GetSection(EdusoOptions.SectionName));
builder.Services.AddDataProtection()
    .SetApplicationName("UtcAttendance.Api")
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath));
var authSettings = AuthSettings.Create(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(authSettings);
builder.Services.AddSingleton<AuthTokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = authSettings.Issuer,
        ValidateAudience = true,
        ValidAudience = authSettings.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(authSettings.SigningKey),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var subject = context.Principal?.FindFirst("sub")?.Value;
            if (!Guid.TryParse(subject, out var accountId))
            {
                context.Fail("Invalid session.");
                return;
            }

            var db = context.HttpContext.RequestServices.GetRequiredService<AttendanceDbContext>();
            var valid = await db.StudentAccounts.AsNoTracking().AnyAsync(
                account => account.Id == accountId,
                context.HttpContext.RequestAborted);
            if (!valid) context.Fail("Tài khoản không tồn tại.");
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddDbContext<AttendanceDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHttpClient<EdusoWidgetClient>(client =>
{
    client.BaseAddress = new Uri("https://aiapi.eduso.vn/api/v1/");
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.Add("Origin", "https://trolyai.eduso.vn");
    client.DefaultRequestHeaders.Add("Referer", "https://trolyai.eduso.vn/");
});
builder.Services.AddScoped<AttendanceSyncService>();
builder.Services.AddScoped<AttendanceWorker>();
builder.Services.AddScoped<EdusoAccountSessionService>();
builder.Services.AddSingleton<LpHarvestWorkflowService>();
builder.Services.AddHangfire(configuration => configuration
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(connectionString)));
builder.Services.AddHangfireServer();
builder.Services.AddControllers();
builder.Services.AddCors(options => options.AddPolicy("dashboard", policy => policy
    .WithOrigins(builder.Configuration["FrontendOrigin"] ?? "http://localhost:3000")
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("dashboard");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"StudentAccounts\" ADD COLUMN IF NOT EXISTS \"AttendanceLeadTimeMinutes\" integer NOT NULL DEFAULT 5;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"StudentAccounts\" ADD COLUMN IF NOT EXISTS \"IsConnected\" boolean NOT NULL DEFAULT true;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"StudentAccounts\" ADD COLUMN IF NOT EXISTS \"ActiveSessionId\" uuid NULL;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"StudentAccounts\" ADD COLUMN IF NOT EXISTS \"EdusoPasswordProtected\" text NULL;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"LiveSessions\" ADD COLUMN IF NOT EXISTS \"JobScheduledAtUtc\" timestamp with time zone NULL;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"LiveSessions\" ADD COLUMN IF NOT EXISTS \"IsManualJob\" boolean NOT NULL DEFAULT false;");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"AttendanceAttempts\" ADD COLUMN IF NOT EXISTS \"DetailsJson\" text NULL;");
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "LearningProgressChecks" (
            "Id" uuid PRIMARY KEY,
            "StudentAccountId" uuid NOT NULL,
            "CourseId" uuid NOT NULL,
            "CourseCode" text NOT NULL,
            "CourseName" text NOT NULL,
            "Status" character varying(30) NOT NULL,
            "Message" character varying(500) NULL,
            "TotalLp" numeric NULL,
            "DeltaLp" numeric NULL,
            "TotalCeu" numeric NULL,
            "ProgressPct" numeric NULL,
            "LpByTypeJson" text NULL,
            "LessonsJson" text NULL,
            "CreatedAtUtc" timestamp with time zone NOT NULL
        );
        CREATE INDEX IF NOT EXISTS "IX_LearningProgressChecks_StudentAccountId_CreatedAtUtc"
            ON "LearningProgressChecks" ("StudentAccountId", "CreatedAtUtc");
        """);
    var recurringJobs = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    recurringJobs.AddOrUpdate<AttendanceSyncService>(
        "eduso-sync-all-accounts",
        service => service.SyncAllAccountsAsync(CancellationToken.None),
        "*/1 * * * *");
}

app.Run();
