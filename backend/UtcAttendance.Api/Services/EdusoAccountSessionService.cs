using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UtcAttendance.Api.Data;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Services;

public sealed class EdusoAccountSessionService
{
    private readonly AttendanceDbContext _db;
    private readonly EdusoWidgetClient _eduso;
    private readonly IDataProtector _protector;
    private readonly ILogger<EdusoAccountSessionService> _logger;

    // Khóa mã hóa cố định AES-256 để mọi instance backend (dev, prod, docker) đều giải mã được mật khẩu trong DB
    private static readonly byte[] StaticAesKey = SHA256.HashData(
        Encoding.UTF8.GetBytes("UtcAttendanceEdusoSecretKey2026_SecureStorage"));

    // In-memory cache for session tokens: email -> (Token, ExpiresAt)
    private static readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> TokenCache =
        new(StringComparer.OrdinalIgnoreCase);

    public EdusoAccountSessionService(
        AttendanceDbContext db,
        EdusoWidgetClient eduso,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<EdusoAccountSessionService> logger)
    {
        _db = db;
        _eduso = eduso;
        _protector = dataProtectionProvider.CreateProtector("UtcAttendance.EdusoPassword.v1");
        _logger = logger;
    }

    /// <summary>
    /// Mã hóa mật khẩu lưu vào cơ sở dữ liệu bằng AES-256-CBC có IV ngẫu nhiên.
    /// Chuỗi kết quả có tiền tố "enc:v1:" và giải mã độc lập trên mọi server.
    /// </summary>
    public string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;

        using var aes = Aes.Create();
        aes.Key = StaticAesKey;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        var plainBytes = Encoding.UTF8.GetBytes(password);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        var combined = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, aes.IV.Length, cipherBytes.Length);

        return "enc:v1:" + Convert.ToBase64String(combined);
    }

    /// <summary>
    /// Giải mã mật khẩu lấy từ cơ sở dữ liệu.
    /// Hỗ trợ cả chuẩn mới "enc:v1:", chuẩn cũ DataProtection ("CfDJ8..."), và chuỗi thô.
    /// </summary>
    public string UnprotectPassword(string protectedPassword)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            throw new InvalidOperationException("Mật khẩu trong database trống. Vui lòng đăng nhập lại tài khoản bằng mật khẩu Eduso.");
        }

        // 1. Chuẩn mới AES-256 (enc:v1:)
        if (protectedPassword.StartsWith("enc:v1:", StringComparison.Ordinal))
        {
            try
            {
                var b64 = protectedPassword["enc:v1:".Length..];
                var raw = Convert.FromBase64String(b64);
                if (raw.Length < 17)
                {
                    throw new InvalidOperationException("Dữ liệu mật khẩu mã hóa trong database không hợp lệ.");
                }

                using var aes = Aes.Create();
                aes.Key = StaticAesKey;
                var iv = new byte[16];
                Buffer.BlockCopy(raw, 0, iv, 0, 16);
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                var cipherBytes = new byte[raw.Length - 16];
                Buffer.BlockCopy(raw, 16, cipherBytes, 0, cipherBytes.Length);
                var plainBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException("Không thể giải mã mật khẩu lưu trong cơ sở dữ liệu. Vui lòng đăng nhập lại tài khoản trên giao diện UTC Attend.", ex);
            }
        }

        // 2. Chuẩn cũ ASP.NET DataProtection (CfDJ8...)
        if (protectedPassword.StartsWith("CfDJ8", StringComparison.Ordinal))
        {
            try
            {
                return _protector.Unprotect(protectedPassword);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException("Mật khẩu trong database được mã hóa bằng khóa máy chủ cũ và không thể giải mã. Vui lòng đăng nhập lại tài khoản trên giao diện UTC Attend.", ex);
            }
        }

        // 3. Chuỗi plain text
        return protectedPassword;
    }

    public static void InvalidateCache(string email)
    {
        TokenCache.TryRemove(email, out _);
    }

    public async Task<string> CreateSessionAsync(StudentAccount account, CancellationToken cancellationToken)
        => await CreateSessionCoreAsync(account.Email, account.EdusoPasswordProtected, account.Id, cancellationToken);

    public Task<string> CreateSessionAsync(string email, string? protectedPassword, CancellationToken cancellationToken)
        => CreateSessionCoreAsync(email, protectedPassword, null, cancellationToken);

    public async Task<string> CreateSessionForAccountAsync(Guid accountId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var account = await _db.StudentAccounts
            .Where(x => x.Id == accountId)
            .Select(x => new { x.Id, x.Email, x.EdusoPasswordProtected })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tài khoản sinh viên không tồn tại trong hệ thống.");

        if (forceRefresh)
        {
            InvalidateCache(account.Email);
        }

        return await CreateSessionCoreAsync(account.Email, account.EdusoPasswordProtected, account.Id, cancellationToken);
    }

    private async Task<string> CreateSessionCoreAsync(string email, string? protectedPassword, Guid? accountId, CancellationToken cancellationToken)
    {
        // 1. Kiểm tra cache còn hạn hay không (trừ hao 5 phút)
        if (TokenCache.TryGetValue(email, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return cached.Token;
        }

        if (string.IsNullOrWhiteSpace(protectedPassword))
        {
            throw new InvalidOperationException($"Tài khoản {email} chưa có mật khẩu trong cơ sở dữ liệu. Vui lòng đăng nhập lại trên UTC Attend.");
        }

        // 2. Lấy đúng mật khẩu trong database ra giải mã
        var password = UnprotectPassword(protectedPassword);

        // 3. Đăng nhập Eduso bằng chính mật khẩu lưu trong DB
        try
        {
            var token = await _eduso.CreateSessionAsync(email, password, cancellationToken);
            TokenCache[email] = (token, DateTimeOffset.UtcNow.AddHours(23));

            // Nếu mật khẩu trong DB đang ở dạng cũ (DataProtection), tự động nâng cấp sang chuẩn AES mới
            if (!protectedPassword.StartsWith("enc:v1:", StringComparison.Ordinal) && accountId.HasValue)
            {
                _ = MigrateToNewFormatAsync(accountId.Value, password);
            }

            return token;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _logger.LogWarning("Mật khẩu lưu trong cơ sở dữ liệu của {Email} bị Eduso từ chối.", MaskEmail(email));
            throw new InvalidOperationException($"Mật khẩu lưu trong cơ sở dữ liệu của tài khoản {email} không chính xác hoặc đã đổi trên Eduso. Vui lòng đăng nhập lại trên UTC Attend.");
        }
    }

    private async Task MigrateToNewFormatAsync(Guid accountId, string rawPassword)
    {
        try
        {
            var newCipher = ProtectPassword(rawPassword);
            var account = await _db.StudentAccounts.FirstOrDefaultAsync(x => x.Id == accountId);
            if (account != null)
            {
                account.EdusoPasswordProtected = newCipher;
                account.IsConnected = true;
                await _db.SaveChangesAsync();
                _logger.LogInformation("[+] Đã tự động nâng cấp mã hóa mật khẩu trong cơ sở dữ liệu cho {Email}.", MaskEmail(account.Email));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Không thể tự động nâng cấp mật khẩu sang định dạng mới.");
        }
    }

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 1) return "[redacted]";
        return $"{email[0]}***{email[at..]}";
    }
}
