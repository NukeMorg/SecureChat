using System;
using Microsoft.Extensions.Options;
using SecureChat.Server;
using SecureChat.Server.Auth;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Тесты SessionRepository: создание строк по jti, валидация (подпись + строка +
/// активность), ревокация одной и всех сессий пользователя.
/// </summary>
[Collection(nameof(ServerDatabaseCollection))] // TempDbDirectory переключает CWD процесса
public class SessionRepositoryTests : IDisposable
{
    private readonly TempDbDirectory _tempDir;
    private readonly Database _db;
    private readonly IJwtTokenService _tokens;
    private readonly ISessionRepository _sessions;

    public SessionRepositoryTests()
    {
        _tempDir = new TempDbDirectory();
        _db = new Database(_tempDir.ConnectionString);
        _db.Initialize();
        _tokens = new JwtTokenService(Options.Create(new JwtOptions
        {
            Secret = "session-repo-test-secret-0123456789abcdef-0123456789abcdef"
        }));
        _sessions = new SessionRepository(_db.ConnectionString, _tokens);
        _sessions.Initialize();
    }

    public void Dispose() => _tempDir.Dispose();

    private int RegisterUser(string email) =>
        _db.RegisterUser(email, "Passw0rd!x", "User", "PK")
        ?? throw new InvalidOperationException($"RegisterUser failed for {email}");

    [Fact]
    public void Create_Validate_RoundTrip()
    {
        int userId = RegisterUser("sess@test.local");
        var issued = _tokens.Issue(userId, "User", "Employee");
        _sessions.Create(userId, issued);

        Assert.Equal(userId, _sessions.Validate(issued.Token));
    }

    [Fact]
    public void Validate_UnknownToken_ReturnsNull() =>
        Assert.Null(_sessions.Validate("no-such-token"));

    [Fact]
    public void Validate_TamperedToken_ReturnsNull()
    {
        int userId = RegisterUser("tamper@test.local");
        var issued = _tokens.Issue(userId, "User", "Employee");
        _sessions.Create(userId, issued);

        // строка сессии существует, но подпись JWT сломана
        string tampered = issued.Token[..^3] + "xxx";
        Assert.Null(_sessions.Validate(tampered));
    }

    [Fact]
    public void Validate_ExpiredSession_ReturnsNull()
    {
        int userId = RegisterUser("expired@test.local");
        var issued = new IssuedToken(_tokens.Issue(userId, "User", "Employee").Token,
            Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddMinutes(-1));
        _sessions.Create(userId, issued);

        Assert.Null(_sessions.Validate(issued.Token));
    }

    [Fact]
    public void Validate_InactiveUser_ReturnsNull()
    {
        int userId = RegisterUser("banned@test.local");
        var issued = _tokens.Issue(userId, "User", "Employee");
        _sessions.Create(userId, issued);

        _db.SetUserActive(userId, false);

        Assert.Null(_sessions.Validate(issued.Token));
    }

    [Fact]
    public void Revoke_SessionBecomesInvalid()
    {
        int userId = RegisterUser("revoke@test.local");
        var issued = _tokens.Issue(userId, "User", "Employee");
        _sessions.Create(userId, issued);

        _sessions.Revoke(issued.Token);

        Assert.Null(_sessions.Validate(issued.Token));
    }

    [Fact]
    public void RevokeAllForUser_AllSessionsInvalid()
    {
        int userId = RegisterUser("revokeall@test.local");
        var issued1 = _tokens.Issue(userId, "User", "Employee");
        var issued2 = _tokens.Issue(userId, "User", "Employee");
        _sessions.Create(userId, issued1);
        _sessions.Create(userId, issued2);

        _sessions.RevokeAllForUser(userId);

        Assert.Null(_sessions.Validate(issued1.Token));
        Assert.Null(_sessions.Validate(issued2.Token));
    }
}
