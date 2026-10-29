using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using UtcAttendance.Api.Domain;

namespace UtcAttendance.Api.Services;

public sealed class AuthSettings
{
    public const string SectionName = "Auth";
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required byte[] SigningKey { get; init; }
    public static AuthSettings Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredKey = configuration[$"{SectionName}:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("Set Auth:JwtSigningKey before starting the production API.");

            configuredKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }

        var key = Encoding.UTF8.GetBytes(configuredKey);
        if (key.Length < 32)
            throw new InvalidOperationException("Auth:JwtSigningKey must be at least 32 characters.");

        return new AuthSettings
        {
            Issuer = configuration[$"{SectionName}:Issuer"] ?? "utc-attendance-api",
            Audience = configuration[$"{SectionName}:Audience"] ?? "utc-attendance-web",
            SigningKey = key
        };
    }
}

public sealed class AuthTokenService(AuthSettings settings)
{
    public string Issue(StudentAccount account, Guid sessionId)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, account.Email),
            new Claim(JwtRegisteredClaimNames.Jti, sessionId.ToString())
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(settings.SigningKey), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience, claims, DateTime.UtcNow, DateTime.UtcNow.AddDays(14), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
