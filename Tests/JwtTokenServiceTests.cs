using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SecureChat.Server.Auth;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Тесты JwtTokenService: выпуск/валидация (HS256), сохранность claims,
/// отклонение подделок — подпись, срок, issuer, секрет.
/// </summary>
public class JwtTokenServiceTests
{
    private static JwtOptions BuildOptions(string secret = "", string issuer = "SecureChat.Server") => new()
    {
        Secret = secret,
        Issuer = issuer,
        Audience = "SecureChat.Client",
        LifetimeDays = 7
    };

    private static IJwtTokenService CreateService(JwtOptions options) =>
        new JwtTokenService(Options.Create(options));

    private static string BuildToken(string secret, string issuer, DateTime notBefore, DateTime expires)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: "SecureChat.Client",
            claims: [new Claim("sub", "1"), new Claim("jti", Guid.NewGuid().ToString("N"))],
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    [Fact]
    public void Issue_Validate_RoundTrip_PreservesClaims()
    {
        var service = CreateService(BuildOptions());
        var issued = service.Issue(42, "Alice", "Manager");

        Assert.False(string.IsNullOrEmpty(issued.Token));
        Assert.False(string.IsNullOrEmpty(issued.Jti));
        Assert.True(issued.ExpiresAtUtc > DateTime.UtcNow);

        var info = service.Validate(issued.Token);
        Assert.NotNull(info);
        Assert.Equal(42, info!.UserId);
        Assert.Equal(issued.Jti, info.Jti);
        Assert.Equal("Alice", info.UserName);
        Assert.Equal("Manager", info.Role);
    }

    [Fact]
    public void Validate_TamperedPayload_ReturnsNull()
    {
        var service = CreateService(BuildOptions());
        var issued = service.Issue(1, "A", "Employee");

        // заменяем символ в середине payload — подпись перестаёт сходиться
        int payloadStart = issued.Token.IndexOf('.') + 1;
        int payloadEnd = issued.Token.LastIndexOf('.');
        int pos = payloadStart + (payloadEnd - payloadStart) / 2;
        var chars = issued.Token.ToCharArray();
        chars[pos] = chars[pos] == 'A' ? 'B' : 'A';

        Assert.Null(service.Validate(new string(chars)));
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsNull()
    {
        var service = CreateService(BuildOptions()); // dev-секрет
        string expired = BuildToken(JwtTokenService.DevelopmentSecret, "SecureChat.Server",
            DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1));

        Assert.Null(service.Validate(expired));
    }

    [Fact]
    public void Validate_WrongIssuer_ReturnsNull()
    {
        var service = CreateService(BuildOptions(issuer: "SecureChat.Server"));
        var foreign = CreateService(BuildOptions(issuer: "Other.Issuer"));

        var issued = foreign.Issue(1, "A", "Employee");

        Assert.Null(service.Validate(issued.Token));
    }

    [Fact]
    public void Validate_WrongSecret_ReturnsNull()
    {
        var service = CreateService(BuildOptions(secret: "correct-secret-correct-secret-correct-1234"));
        var rogue = CreateService(BuildOptions(secret: "rogue-secret-rogue-secret-rogue-secret-123456"));

        var issued = rogue.Issue(1, "A", "Employee");

        Assert.Null(service.Validate(issued.Token));
    }

    [Fact]
    public void Validate_NullOrEmpty_ReturnsNull()
    {
        var service = CreateService(BuildOptions());
        Assert.Null(service.Validate(""));
        Assert.Null(service.Validate(null!));
        Assert.Null(service.Validate("not-a-jwt"));
    }

    [Fact]
    public void Issue_LifetimeFromOptions()
    {
        var service = CreateService(BuildOptions());
        var issued = service.Issue(1, "A", "Employee");

        // LifetimeDays = 7 (дефолт) с допуском на округление времени выпуска
        double days = (issued.ExpiresAtUtc - DateTime.UtcNow).TotalDays;
        Assert.InRange(days, 6.9, 7.01);
    }
}
