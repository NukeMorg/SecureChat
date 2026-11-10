using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Server.Auth;

/// <summary>
/// Фасад над JwtSecurityTokenHandler: выпуск и валидация JWT сессий (HS256).
/// Claims: sub (userId), jti (id сессии для ревокации), name, role (информационные).
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<JwtTokenService>();

    private static readonly JwtSecurityTokenHandler Handler = new() { MapInboundClaims = false };

    /// <summary>Детерминированный секрет локальной разработки; продакшен — Jwt:Secret в user-secrets/env.</summary>
    public const string DevelopmentSecret =
        "SecureChat-Development-Secret-0123456789abcdef-DO-NOT-USE-IN-PRODUCTION";

    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;

        string secret = _options.Secret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            secret = DevelopmentSecret;
            Log.Warning("Jwt:Secret не задан — используется встроенный секрет разработки. " +
                        "Для продакшена задайте Jwt:Secret через user-secrets или переменные окружения.");
        }
        // HS256 требует ключа не короче хеша (256 бит = 32 байта UTF-8)
        if (secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret слишком короткий: минимум 32 символа.");

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public IssuedToken Issue(int userId, string userName, string role)
    {
        string jti = Guid.NewGuid().ToString("N");
        DateTime now = DateTime.UtcNow;
        DateTime expires = now.AddDays(Math.Max(1, _options.LifetimeDays));

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                new Claim("sub", userId.ToString()),
                new Claim("jti", jti),
                new Claim("name", userName),
                new Claim("role", role)
            ],
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        string token = Handler.WriteToken(jwt);
        Log.Debug("JWT issued userId={UserId} jti={Jti} expires={Expires:O}", userId, jti, expires);
        return new IssuedToken(token, jti, expires);
    }

    public TokenInfo? Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        try
        {
            var principal = Handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _options.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                // минимальный допуск расхождения часов вместо дефолтных 5 минут
                ClockSkew = TimeSpan.FromSeconds(30)
            }, out _);

            string? sub = principal.FindFirst("sub")?.Value;
            string? jti = principal.FindFirst("jti")?.Value;
            if (!int.TryParse(sub, out int userId) || string.IsNullOrEmpty(jti)) return null;

            return new TokenInfo(
                userId,
                jti,
                principal.FindFirst("name")?.Value ?? "",
                principal.FindFirst("role")?.Value ?? "");
        }
        catch (Exception ex) // SecurityTokenExpiredException, SecurityTokenSignatureKeyValidation и др.
        {
            Log.Debug("JWT validation failed: {Reason}", ex.Message);
            return null;
        }
    }
}
