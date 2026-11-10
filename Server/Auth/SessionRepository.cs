using System;
using Microsoft.Data.Sqlite;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Server.Auth;

/// <summary>SQLite-реализация <see cref="ISessionRepository"/> (единственный владелец SQL сессий).</summary>
public class SessionRepository : ISessionRepository
{
    private readonly string _connectionString;
    private readonly IJwtTokenService _tokens;

    private static readonly ILogger Log = Serilog.Log.ForContext<SessionRepository>();

    public SessionRepository(string connectionString, IJwtTokenService tokens)
    {
        _connectionString = connectionString;
        _tokens = tokens;
    }

    public void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Sessions (
                Jti TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Clients(Id)
            );";
        cmd.ExecuteNonQuery();
    }

    public void Create(int userId, IssuedToken issued)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Sessions (Jti, UserId, CreatedAt, ExpiresAt)
            VALUES (@jti, @uid, @time, @expires)";
        cmd.Parameters.AddWithValue("@jti", issued.Jti);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@expires", issued.ExpiresAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public int? Validate(string token)
    {
        // подпись, issuer и срок жизни — криптографическая валидация
        var info = _tokens.Validate(token);
        if (info == null) return null;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        Cleanup(conn);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.UserId FROM Sessions s
            JOIN Clients c ON c.Id = s.UserId
            WHERE s.Jti = @jti AND s.ExpiresAt > @now AND c.IsActive = 1";
        cmd.Parameters.AddWithValue("@jti", info.Jti);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    public void Revoke(string token)
    {
        // недействительный токен и так не пройдёт Hello — ревокация не нужна
        var info = _tokens.Validate(token);
        if (info == null) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE Jti = @jti";
        cmd.Parameters.AddWithValue("@jti", info.Jti);
        cmd.ExecuteNonQuery();
        Log.Debug("Session revoked jti={Jti}", info.Jti);
    }

    public void RevokeAllForUser(int userId)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE UserId = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    private static void Cleanup(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE ExpiresAt <= @now";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
