using System.Net;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;
using UtcAttendance.Api.Services;

namespace UtcAttendance.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AttendanceDbContext db,
    EdusoWidgetClient eduso,
    EdusoAccountSessionService accountSessions,
    AuthTokenService tokens,
    IBackgroundJobClient jobs) : ControllerBase
{
    [HttpPost("register")]
    public Task<ActionResult> Register([FromBody] AuthRequest request, CancellationToken cancellationToken)
        => AuthenticateAsync(request, createIfMissing: true, cancellationToken);

    [HttpPost("login")]
    public Task<ActionResult> Login([FromBody] AuthRequest request, CancellationToken cancellationToken)
        => AuthenticateAsync(request, createIfMissing: false, cancellationToken);

    private async Task<ActionResult> AuthenticateAsync(AuthRequest request, bool createIfMissing, CancellationToken cancellationToken)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return BadRequest(new { message = "Nhập email UTC hợp lệ." });
        if (string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { message = "Nhập mật khẩu Eduso." });

        var account = await db.StudentAccounts.SingleOrDefaultAsync(x => x.Email == email, cancellationToken);
        if (createIfMissing && account is not null)
            return Conflict(new { message = "Email này đã đăng ký UTC Attend. Hãy đăng nhập." });
        if (!createIfMissing && account is null)
            return NotFound(new { message = "Email này chưa đăng ký UTC Attend. Hãy đăng ký trước." });

        try
        {
            // Verify the submitted Eduso credentials directly; never log or return the password or remote token.
            await eduso.CreateSessionAsync(email, request.Password, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Unauthorized(new { message = "Email hoặc mật khẩu Eduso không đúng." });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Không thể kết nối Eduso lúc này. Hãy thử lại sau." });
        }

        account ??= new StudentAccount { Email = email, AutoAttendanceEnabled = true, IsConnected = true };
        account.EdusoPasswordProtected = accountSessions.ProtectPassword(request.Password);
        account.ActiveSessionId = Guid.NewGuid();
        account.IsConnected = true;
        if (db.Entry(account).State == EntityState.Detached)
            db.StudentAccounts.Add(account);

        await db.SaveChangesAsync(cancellationToken);
        jobs.Enqueue<AttendanceSyncService>(service => service.SyncAccountAsync(account.Id, CancellationToken.None));

        return Ok(new { token = tokens.Issue(account, account.ActiveSessionId.Value), account = new { account.Email } });
    }
}

public sealed record AuthRequest(string? Email, string? Password);
