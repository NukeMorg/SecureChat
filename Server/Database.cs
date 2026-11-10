using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SecureChat.Core;
using SecureChat.Security;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Server;

public class Database : IDatabase
{
    private readonly string cs;
    public string ConnectionString => cs;
    private static readonly ILogger Log = Serilog.Log.ForContext<Database>();

    public Database() : this("Data Source=server.db;Default Timeout=10;")
    {
    }

    public Database(string connectionString)
    {
        cs = connectionString;
    }

    public void Initialize()
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS Clients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT UNIQUE,
                PasswordHash TEXT,
                Salt TEXT,
                PublicKey TEXT NOT NULL,
                UserName TEXT DEFAULT 'User',
                Role TEXT NOT NULL DEFAULT 'Employee',
                IsActive INTEGER NOT NULL DEFAULT 1,
                Status TEXT NOT NULL DEFAULT '',
                AvatarPath TEXT
            );
            CREATE TABLE IF NOT EXISTS EmailVerifications (
                Email TEXT PRIMARY KEY,
                Code TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                Jti TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Clients(Id)
            );
            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId TEXT UNIQUE NOT NULL,
                SenderId INTEGER NOT NULL,
                ReceiverId INTEGER NOT NULL,
                EncryptedKey TEXT,
                EncryptedKeySelf TEXT,
                Nonce TEXT,
                Ciphertext TEXT,
                Tag TEXT,
                ContentType TEXT NOT NULL,
                FileName TEXT,
                FileSize INTEGER DEFAULT 0,
                FileTransferId TEXT,
                Timestamp TEXT NOT NULL,
                Status TEXT DEFAULT 'Sent',
                FOREIGN KEY (SenderId) REFERENCES Clients(Id),
                FOREIGN KEY (ReceiverId) REFERENCES Clients(Id)
            );
            CREATE TABLE IF NOT EXISTS Contacts (
                UserId INTEGER NOT NULL,
                ContactId INTEGER NOT NULL,
                PRIMARY KEY (UserId, ContactId),
                FOREIGN KEY (UserId) REFERENCES Clients(Id),
                FOREIGN KEY (ContactId) REFERENCES Clients(Id)
            );
            CREATE TABLE IF NOT EXISTS EmailRateLimits (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT NOT NULL,
                LastSentAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_email_rate ON EmailRateLimits(Email, LastSentAt);
            CREATE TABLE IF NOT EXISTS LoginAttempts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Email TEXT NOT NULL,
                IpAddress TEXT NOT NULL,
                AttemptedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_login_attempts ON LoginAttempts(Email, AttemptedAt);";
        cmd.ExecuteNonQuery();

        using (var sysCmd = conn.CreateCommand())
        {
            sysCmd.CommandText = @"
                INSERT OR IGNORE INTO Clients (Id, Email, PasswordHash, Salt, PublicKey, UserName, Role, IsActive)
                VALUES (0, 'system@channel.internal', '', '', 'SYSTEM', 'Channel System', 'System', 0);";
            sysCmd.ExecuteNonQuery();
        }

        Migrate(conn);
        var csb = new SqliteConnectionStringBuilder(cs);
        Log.Information("Database initialized: {DbPath}", Path.GetFullPath(csb.DataSource));
    }

    /// <summary>Миграции существующей схемы: версии через PRAGMA user_version.</summary>
    private void Migrate(SqliteConnection conn)
    {
        long version = GetUserVersion(conn);

        if (version < 1)
        {
            ApplyV1(conn); // сессии: TTL/ревокация; клиенты: роль/активность/статус/аватар; AuditLog
        }
        if (version < 2)
        {
            ApplyV2(conn); // каналы: Channels/ChannelMembers/ChannelKeys; сообщения: ChannelId и др.
        }
        if (version < 3)
        {
            ApplyV3(conn); // роль HR переименована в Manager (данные + протокол)
        }
        if (version < 4)
        {
            ApplyV4(conn); // сессии: plaintext-токены → JWT (ключ jti)
        }

        SetUserVersion(conn, Math.Max(version, 4));
    }

    /// <summary>Миграция v3: переименование роли HR → Manager во всех данных.</summary>
    private void ApplyV3(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET Role = 'Manager' WHERE Role = 'HR';";
        int rows = cmd.ExecuteNonQuery();
        Log.Information("Role renamed HR to Manager for {Count} clients", rows);
    }

    /// <summary>
    /// Миграция v4: сессии — plaintext-токены → JWT (первичный ключ jti).
    /// Все старые сессии недействительны — пользователи входят заново.
    /// </summary>
    private void ApplyV4(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DROP TABLE IF EXISTS Sessions;
            CREATE TABLE Sessions (
                Jti TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Clients(Id)
            );";
        cmd.ExecuteNonQuery();
        Log.Information("Sessions migrated to JWT (jti) schema");
    }

    /// <summary>
    /// Миграция v1: роли Admin/HR/Employee, блокировка, статус, аватар,
    /// таблица AuditLog (append-only — триггеры запрещают UPDATE/DELETE).
    /// </summary>
    private void ApplyV1(SqliteConnection conn)
    {
        bool addedExpiresAt = TryAddColumn(conn, "Sessions", "ExpiresAt", "TEXT");
        TryAddColumn(conn, "Sessions", "IsRevoked", "INTEGER NOT NULL DEFAULT 0");
        if (addedExpiresAt)
        {
            using var backfill = conn.CreateCommand();
            backfill.CommandText = "UPDATE Sessions SET ExpiresAt = @e WHERE ExpiresAt IS NULL";
            backfill.Parameters.AddWithValue("@e", DateTime.UtcNow.AddDays(7).ToString("o"));
            backfill.ExecuteNonQuery();
        }

        TryAddColumn(conn, "Clients", "Role", "TEXT NOT NULL DEFAULT 'Employee'");
        TryAddColumn(conn, "Clients", "IsActive", "INTEGER NOT NULL DEFAULT 1");
        TryAddColumn(conn, "Clients", "Status", "TEXT NOT NULL DEFAULT ''");
        TryAddColumn(conn, "Clients", "AvatarPath", "TEXT");

        using (var audit = conn.CreateCommand())
        {
            audit.CommandText = @"
                CREATE TABLE IF NOT EXISTS AuditLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    ActorId INTEGER,
                    Action TEXT NOT NULL,
                    TargetId INTEGER,
                    Result TEXT NOT NULL DEFAULT 'Ok',
                    IpAddress TEXT NOT NULL DEFAULT '',
                    UserAgent TEXT NOT NULL DEFAULT ''
                );
                CREATE INDEX IF NOT EXISTS idx_auditlog_time ON AuditLog(Timestamp);
                CREATE INDEX IF NOT EXISTS idx_auditlog_actor ON AuditLog(ActorId);
                CREATE TRIGGER IF NOT EXISTS trg_auditlog_no_update BEFORE UPDATE ON AuditLog
                BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;
                CREATE TRIGGER IF NOT EXISTS trg_auditlog_no_delete BEFORE DELETE ON AuditLog
                BEGIN SELECT RAISE(ABORT, 'AuditLog is append-only'); END;";
            audit.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Миграция v2: каналы с E2E-групповым ключом.
    /// GroupKey хранится только в зашифрованном виде (RSA 1-1 каждого участника).
    /// </summary>
    private void ApplyV2(SqliteConnection conn)
    {
        using (var channels = conn.CreateCommand())
        {
            channels.CommandText = @"
                CREATE TABLE IF NOT EXISTS Channels (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL UNIQUE,
                    OwnerId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    FOREIGN KEY (OwnerId) REFERENCES Clients(Id)
                );
                CREATE TABLE IF NOT EXISTS ChannelMembers (
                    ChannelId TEXT NOT NULL,
                    UserId INTEGER NOT NULL,
                    Role TEXT NOT NULL DEFAULT 'Member',
                    JoinedAt TEXT NOT NULL,
                    PRIMARY KEY (ChannelId, UserId),
                    FOREIGN KEY (ChannelId) REFERENCES Channels(Id),
                    FOREIGN KEY (UserId) REFERENCES Clients(Id)
                );
                CREATE INDEX IF NOT EXISTS idx_channelmembers_user ON ChannelMembers(UserId);
                CREATE TABLE IF NOT EXISTS ChannelKeys (
                    ChannelId TEXT NOT NULL,
                    UserId INTEGER NOT NULL,
                    Version INTEGER NOT NULL,
                    EncryptedKey TEXT NOT NULL,
                    PRIMARY KEY (ChannelId, UserId)
                );";
            channels.ExecuteNonQuery();
        }

        TryAddColumn(conn, "Messages", "ChannelId", "TEXT");
        TryAddColumn(conn, "Messages", "ChainIteration", "INTEGER");
        TryAddColumn(conn, "Messages", "ReplyToMessageId", "TEXT");
        TryAddColumn(conn, "Messages", "IsEdited", "INTEGER NOT NULL DEFAULT 0");
        TryAddColumn(conn, "Messages", "EditedAt", "TEXT");
        TryAddColumn(conn, "Messages", "DeletedAt", "TEXT");

        using (var idx = conn.CreateCommand())
        {
            idx.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_messages_channel ON Messages(ChannelId, Timestamp);";
            idx.ExecuteNonQuery();
        }
    }

    private static long GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void SetUserVersion(SqliteConnection conn, long version)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static bool TryAddColumn(SqliteConnection conn, string table, string column, string type)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            return false; // колонка уже существует
        }
    }

    public void SaveVerificationCode(string email, string code, TimeSpan ttl)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO EmailVerifications (Email, Code, ExpiresAt)
            VALUES (@email, @code, @expires)
            ON CONFLICT(Email) DO UPDATE SET Code = @code, ExpiresAt = @expires;";
        cmd.Parameters.AddWithValue("@email", normEmail);
        cmd.Parameters.AddWithValue("@code", code);
        cmd.Parameters.AddWithValue("@expires", DateTime.UtcNow.Add(ttl).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public CodeVerifyResult VerifyAndConsumeCode(string email, string code)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Code, ExpiresAt FROM EmailVerifications WHERE Email = @email";
        cmd.Parameters.AddWithValue("@email", normEmail);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return CodeVerifyResult.Invalid;

        string savedCode = reader.GetString(0);
        DateTime expiresAt = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        if (DateTime.UtcNow > expiresAt)
        {
            reader.Close();
            DeleteVerificationCode(normEmail);
            return CodeVerifyResult.Expired;
        }
        if (savedCode != code.Trim()) return CodeVerifyResult.Invalid;

        reader.Close();
        DeleteVerificationCode(normEmail);
        return CodeVerifyResult.Valid;
    }

    private void DeleteVerificationCode(string email)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM EmailVerifications WHERE Email = @email";
        cmd.Parameters.AddWithValue("@email", email);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Блокировка брутфорса: не более maxAttempts неудачных входов за window по email ИЛИ по IP.</summary>
    public bool IsLoginBlocked(string email, string ipAddress, TimeSpan window, int maxAttempts)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        CleanupLoginAttempts(conn, window);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM LoginAttempts
            WHERE AttemptedAt > @cutoff AND (Email = @email OR IpAddress = @ip)";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(window).ToString("o"));
        cmd.Parameters.AddWithValue("@email", normEmail);
        cmd.Parameters.AddWithValue("@ip", ipAddress);
        return (long)cmd.ExecuteScalar()! >= maxAttempts;
    }

    public void RecordFailedLogin(string email, string ipAddress)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO LoginAttempts (Email, IpAddress, AttemptedAt)
            VALUES (@email, @ip, @time)";
        cmd.Parameters.AddWithValue("@email", normEmail);
        cmd.Parameters.AddWithValue("@ip", ipAddress);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static void CleanupLoginAttempts(SqliteConnection conn, TimeSpan window)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM LoginAttempts WHERE AttemptedAt <= @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(window).ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public bool TryReserveEmailSend(string email, TimeSpan window, int maxAttempts)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using (var cleanCmd = conn.CreateCommand())
        {
            cleanCmd.CommandText = "DELETE FROM EmailRateLimits WHERE LastSentAt < @cutoff";
            cleanCmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(window).ToString("o"));
            cleanCmd.ExecuteNonQuery();
        }

        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO EmailRateLimits (Email, LastSentAt)
            SELECT @email, @time
            WHERE (SELECT COUNT(*) FROM EmailRateLimits WHERE Email = @email) < @max";
        insertCmd.Parameters.AddWithValue("@email", normEmail);
        insertCmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        insertCmd.Parameters.AddWithValue("@max", maxAttempts);
        return insertCmd.ExecuteNonQuery() > 0;
    }

    private static (string hash, string salt) HashPassword(string password)
    {
        // Новые хэши — BCrypt; соль хранится внутри строки хэша.
        return (PasswordHasher.Hash(password), string.Empty);
    }

    private void UpgradePasswordHash(int userId, string bcryptHash)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET PasswordHash = @hash, Salt = NULL WHERE Id = @id";
        cmd.Parameters.AddWithValue("@hash", bcryptHash);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
        Log.Debug("Password hash upgraded to BCrypt for userId={UserId}", userId);
    }

    public int? RegisterUser(string email, string password, string userName, string publicKey)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM Clients WHERE LOWER(Email) = @email";
        checkCmd.Parameters.AddWithValue("@email", normEmail);
        if ((long)checkCmd.ExecuteScalar()! > 0) return null;

        var (hash, salt) = HashPassword(password);
        var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = @"
            INSERT INTO Clients (Email, PasswordHash, Salt, UserName, PublicKey)
            VALUES (@email, @hash, @salt, @name, @key);
            SELECT last_insert_rowid();";
        insertCmd.Parameters.AddWithValue("@email", normEmail);
        insertCmd.Parameters.AddWithValue("@hash", hash);
        insertCmd.Parameters.AddWithValue("@salt", salt);
        insertCmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(userName) ? normEmail : userName);
        insertCmd.Parameters.AddWithValue("@key", publicKey);

        int id = Convert.ToInt32(insertCmd.ExecuteScalar());
        Log.Debug("RegisterUser created userId={UserId}", id);
        return id;
    }

    public (int id, string name)? Authenticate(string email, string password)
    {
        string normEmail = email.Trim().ToLowerInvariant();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, UserName, PasswordHash, Salt FROM Clients WHERE LOWER(Email) = @email";
        cmd.Parameters.AddWithValue("@email", normEmail);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            int id = reader.GetInt32(0);
            string name = reader.GetString(1);
            string hash = reader.GetString(2);
            string salt = reader.IsDBNull(3) ? "" : reader.GetString(3);
            if (PasswordHasher.VerifyAny(password, hash, salt, out string? upgradeHash))
            {
                reader.Close();
                if (upgradeHash != null) UpgradePasswordHash(id, upgradeHash);
                Log.Debug("Authenticate successful for userId={UserId}", id);
                return (id, name);
            }
        }
        return null;
    }

    // ---------- Сессии (JWT: строки хранятся по jti; выпуск и валидация токена — IJwtTokenService) ----------

    public void CreateSession(int userId, string jti, DateTime expiresAtUtc)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Sessions (Jti, UserId, CreatedAt, ExpiresAt)
            VALUES (@jti, @uid, @time, @expires)";
        cmd.Parameters.AddWithValue("@jti", jti);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@expires", expiresAtUtc.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public int? ValidateSession(string jti)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        CleanupSessions(conn);
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.UserId FROM Sessions s
            JOIN Clients c ON c.Id = s.UserId
            WHERE s.Jti = @jti AND s.ExpiresAt > @now AND c.IsActive = 1";
        cmd.Parameters.AddWithValue("@jti", jti);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    /// <summary>Logout: удаление сессии.</summary>
    public void RevokeSession(string jti)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE Jti = @jti";
        cmd.Parameters.AddWithValue("@jti", jti);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Отзыв всех сессий пользователя (смена пароля, бан).</summary>
    public void RevokeAllUserSessions(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE UserId = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.ExecuteNonQuery();
    }

    private static void CleanupSessions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Sessions WHERE ExpiresAt <= @now";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // ---------- Роли, профиль, блокировка ----------

    public string GetUserRole(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Role FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        var result = cmd.ExecuteScalar() as string;
        return string.IsNullOrEmpty(result) ? "Employee" : result;
    }

    public void SetUserRole(int userId, string role)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET Role = @role WHERE Id = @id";
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    public bool IsUserActive(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT IsActive FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        var result = cmd.ExecuteScalar();
        return result == null || Convert.ToInt64(result) == 1;
    }

    public void SetUserActive(int userId, bool isActive)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET IsActive = @active WHERE Id = @id";
        cmd.Parameters.AddWithValue("@active", isActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    public string GetUserStatus(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Status FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    public void SetUserStatus(int userId, string status)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET Status = @status WHERE Id = @id";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    public void SetAvatarPath(int userId, string? path)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET AvatarPath = @path WHERE Id = @id";
        cmd.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    public string? GetAvatarPath(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT AvatarPath FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>Проверка текущего пароля пользователя (с легаси-обновлением хэша).</summary>
    public bool VerifyPasswordForUser(int userId, string password, out string? upgradeHash)
    {
        upgradeHash = null;
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PasswordHash, Salt FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return false;
        string hash = reader.GetString(0);
        string salt = reader.IsDBNull(1) ? "" : reader.GetString(1);
        return PasswordHasher.VerifyAny(password, hash, salt, out upgradeHash);
    }

    /// <summary>Сохранение нового BCrypt-хэша пароля (смена пароля).</summary>
    public void UpdatePassword(int userId, string newPassword)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET PasswordHash = @hash, Salt = NULL WHERE Id = @id";
        cmd.Parameters.AddWithValue("@hash", PasswordHasher.Hash(newPassword));
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    // ---------- Аудит ----------

    public void WriteAudit(int? actorId, string action, int? targetId, string result, string ip, string userAgent = "")
    {
        try
        {
            using var conn = new SqliteConnection(cs);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO AuditLog (Timestamp, ActorId, Action, TargetId, Result, IpAddress, UserAgent)
                VALUES (@time, @actor, @action, @target, @result, @ip, @ua)";
            cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@actor", (object?)actorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@action", action);
            cmd.Parameters.AddWithValue("@target", (object?)targetId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@result", result);
            cmd.Parameters.AddWithValue("@ip", ip);
            cmd.Parameters.AddWithValue("@ua", userAgent);
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex)
        {
            // аудит не должен ломать основной сценарий; событие остаётся в файловом аудит-логе
            Log.Warning(ex, "Failed to write AuditLog action={Action}", action);
        }
    }

    public long GetAuditLogCount()
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM AuditLog";
        return (long)cmd.ExecuteScalar()!;
    }

    public List<AuditLogEntry> GetAuditLog(int limit, int offset)
    {
        var list = new List<AuditLogEntry>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Timestamp, ActorId, Action, TargetId, Result, IpAddress
            FROM AuditLog ORDER BY Id DESC LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AuditLogEntry
            {
                Id = reader.GetInt64(0),
                Timestamp = reader.GetString(1),
                ActorId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Action = reader.GetString(3),
                TargetId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Result = reader.GetString(5),
                IpAddress = reader.GetString(6)
            });
        }
        return list;
    }

    public bool UserExists(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Clients WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public string? GetPublicKey(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PublicKey FROM Clients WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    public void UpdateUserName(int id, string userName)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Clients SET UserName = @name WHERE Id = @id";
        cmd.Parameters.AddWithValue("@name", userName);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public string? GetUserName(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserName FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    public void SaveMessage(string messageId, int senderId, int receiverId,
                            string? encryptedKey, string? nonce, string? ciphertext, string? tag,
                            string contentType, string? fileName, long fileSize, string? fileTransferId,
                            string status = "Sent", string? encryptedKeySelf = null, string? replyToMessageId = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Messages
            (MessageId, SenderId, ReceiverId, EncryptedKey, Nonce, Ciphertext, Tag,
             ContentType, FileName, FileSize, FileTransferId, Timestamp, Status, EncryptedKeySelf, ReplyToMessageId)
            VALUES (@msgId, @sender, @receiver, @encKey, @nonce, @ct, @tag,
                    @ctype, @fname, @fsize, @ftransfer, @time, @status, @encKeySelf, @replyTo)
            ON CONFLICT(MessageId) DO UPDATE SET
                EncryptedKey = excluded.EncryptedKey,
                EncryptedKeySelf = excluded.EncryptedKeySelf,
                Nonce = excluded.Nonce,
                Ciphertext = excluded.Ciphertext,
                Tag = excluded.Tag,
                ContentType = excluded.ContentType,
                FileName = excluded.FileName,
                FileSize = excluded.FileSize,
                FileTransferId = excluded.FileTransferId,
                Timestamp = excluded.Timestamp,
                Status = excluded.Status";
        cmd.Parameters.AddWithValue("@msgId", messageId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@receiver", receiverId);
        cmd.Parameters.AddWithValue("@encKey", (object?)encryptedKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ct", (object?)ciphertext ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ctype", contentType);
        cmd.Parameters.AddWithValue("@fname", (object?)fileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fsize", fileSize);
        cmd.Parameters.AddWithValue("@ftransfer", (object?)fileTransferId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@encKeySelf", (object?)encryptedKeySelf ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@replyTo", (object?)replyToMessageId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
            Log.Warning("SaveMessage slow: {DurationMs}ms messageId={MessageId}", sw.ElapsedMilliseconds, messageId);
    }

    public void UpdateMessageStatus(string messageId, string status)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET Status = @status WHERE MessageId = @msgId";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@msgId", messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Метаданные сообщения для маршрутизации событий edit/delete.</summary>
    public (int senderId, int receiverId, string? channelId, string timestamp)? GetMessageInfo(string messageId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT SenderId, ReceiverId, ChannelId, Timestamp FROM Messages WHERE MessageId = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt32(0), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3));
    }

    /// <summary>Редактирование: новый шифртекст + маркер IsEdited.</summary>
    public void UpdateMessageContent(string messageId, string nonce, string ciphertext, string tag)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Messages
            SET Nonce = @nonce, Ciphertext = @ct, Tag = @tag,
                IsEdited = 1, EditedAt = @time
            WHERE MessageId = @id";
        cmd.Parameters.AddWithValue("@nonce", nonce);
        cmd.Parameters.AddWithValue("@ct", ciphertext);
        cmd.Parameters.AddWithValue("@tag", tag);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Soft-delete: DeletedAt, шифртекст сохраняется.</summary>
    public void SoftDeleteMessage(string messageId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Messages SET DeletedAt = @time WHERE MessageId = @id";
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.ExecuteNonQuery();
    }

    public int? GetSenderByMessageId(string messageId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SenderId FROM Messages WHERE MessageId = @id";
        cmd.Parameters.AddWithValue("@id", messageId);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
    }

    /// <summary>
    /// Страница истории Direct-диалога: до limit сообщений старее курсора (по убыванию времени),
    /// признак HasMore для «Загрузить ещё».
    /// </summary>
    public (List<HistoryMessage> messages, bool hasMore) GetHistoryPage(
        int user1, int user2, string? beforeTimestamp, int limit)
    {
        var list = new List<HistoryMessage>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, SenderId, ReceiverId, EncryptedKey, EncryptedKeySelf, Nonce, Ciphertext, Tag,
                   ContentType, FileName, FileSize, FileTransferId, Timestamp, Status,
                   ReplyToMessageId, IsEdited, DeletedAt
            FROM Messages
            WHERE ChannelId IS NULL
              AND ((SenderId = @user1 AND ReceiverId = @user2)
                OR (SenderId = @user2 AND ReceiverId = @user1))
              AND (@before IS NULL OR Timestamp < @before)
            ORDER BY Timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@user1", user1);
        cmd.Parameters.AddWithValue("@user2", user2);
        cmd.Parameters.AddWithValue("@before", (object?)beforeTimestamp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", limit + 1); // +1 для признака HasMore
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (list.Count >= limit) continue; // лишнюю строку читаем только ради HasMore
            list.Add(new HistoryMessage
            {
                MessageId = reader.GetString(0),
                SenderId = reader.GetInt32(1),
                ReceiverId = reader.GetInt32(2),
                EncryptedKey = reader.IsDBNull(3) ? null : reader.GetString(3),
                EncryptedKeySelf = reader.IsDBNull(4) ? null : reader.GetString(4),
                Nonce = reader.IsDBNull(5) ? null : reader.GetString(5),
                Ciphertext = reader.IsDBNull(6) ? null : reader.GetString(6),
                Tag = reader.IsDBNull(7) ? null : reader.GetString(7),
                ContentType = reader.GetString(8),
                FileName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FileSize = reader.GetInt64(10),
                FileTransferId = reader.IsDBNull(11) ? null : reader.GetString(11),
                Timestamp = reader.GetString(12),
                Status = reader.GetString(13),
                ReplyToMessageId = reader.IsDBNull(14) ? null : reader.GetString(14),
                IsEdited = !reader.IsDBNull(15) && reader.GetInt64(15) == 1,
                IsDeleted = !reader.IsDBNull(16)
            });
        }
        bool hasMore = false;
        if (list.Count > 0)
        {
            hasMore = HasOlderDirectMessages(conn, user1, user2, list[^1].Timestamp);
        }
        list.Reverse(); // клиент ожидает по возрастанию времени
        Log.Debug("GetHistoryPage user1={U1} user2={U2} count={Count} hasMore={HasMore}",
            user1, user2, list.Count, hasMore);
        return (list, hasMore);
    }

    private static bool HasOlderDirectMessages(SqliteConnection conn, int user1, int user2, string oldestTimestamp)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM Messages
            WHERE ChannelId IS NULL
              AND ((SenderId = @u1 AND ReceiverId = @u2) OR (SenderId = @u2 AND ReceiverId = @u1))
              AND Timestamp < @t";
        cmd.Parameters.AddWithValue("@u1", user1);
        cmd.Parameters.AddWithValue("@u2", user2);
        cmd.Parameters.AddWithValue("@t", oldestTimestamp);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public List<HistoryMessage> GetPendingMessages(int receiverId)
    {
        var list = new List<HistoryMessage>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, SenderId, ReceiverId, EncryptedKey, EncryptedKeySelf,
                   Nonce, Ciphertext, Tag, ContentType, FileName, FileSize,
                   FileTransferId, Timestamp, Status
            FROM Messages
            WHERE ReceiverId = @uid AND Status = 'Sent'
            ORDER BY Timestamp ASC";
        cmd.Parameters.AddWithValue("@uid", receiverId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new HistoryMessage
            {
                MessageId = reader.GetString(0),
                SenderId = reader.GetInt32(1),
                ReceiverId = reader.GetInt32(2),
                EncryptedKey = reader.IsDBNull(3) ? null : reader.GetString(3),
                EncryptedKeySelf = reader.IsDBNull(4) ? null : reader.GetString(4),
                Nonce = reader.IsDBNull(5) ? null : reader.GetString(5),
                Ciphertext = reader.IsDBNull(6) ? null : reader.GetString(6),
                Tag = reader.IsDBNull(7) ? null : reader.GetString(7),
                ContentType = reader.GetString(8),
                FileName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FileSize = reader.GetInt64(10),
                FileTransferId = reader.IsDBNull(11) ? null : reader.GetString(11),
                Timestamp = reader.GetString(12),
                Status = reader.GetString(13)
            });
        }
        return list;
    }

    public void AddContact(int userId, int contactId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();

        cmd.CommandText = "INSERT OR IGNORE INTO Contacts (UserId, ContactId) VALUES (@uid, @cid)";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@cid", contactId);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT OR IGNORE INTO Contacts (UserId, ContactId) VALUES (@uid2, @cid2)";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@uid2", contactId);
        cmd.Parameters.AddWithValue("@cid2", userId);
        cmd.ExecuteNonQuery();
    }

    public List<int> GetContactIds(int userId)
    {
        var ids = new List<int>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ContactId FROM Contacts WHERE UserId = @uid";
        cmd.Parameters.AddWithValue("@uid", userId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        return ids;
    }

    // ---------- Каналы ----------

    /// <summary>Создание канала; возвращает null, если имя занято.</summary>
    public string? CreateChannel(string name, int ownerId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Channels (Id, Name, OwnerId, CreatedAt)
            VALUES (@id, @name, @owner, @time);";
        string channelId = Guid.NewGuid().ToString();
        cmd.Parameters.AddWithValue("@id", channelId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        try
        {
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            return null; // UNIQUE(Name)
        }

        var member = conn.CreateCommand();
        member.CommandText = @"
            INSERT INTO ChannelMembers (ChannelId, UserId, Role, JoinedAt)
            VALUES (@id, @owner, 'Owner', @time);";
        member.Parameters.AddWithValue("@id", channelId);
        member.Parameters.AddWithValue("@owner", ownerId);
        member.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        member.ExecuteNonQuery();

        Log.Debug("Channel created channelId={ChannelId} ownerId={OwnerId}", channelId, ownerId);
        return channelId;
    }

    public bool ChannelExists(string channelId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Channels WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", channelId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public string? GetChannelName(string channelId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Channels WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", channelId);
        return cmd.ExecuteScalar() as string;
    }

    public int? GetChannelOwnerId(string channelId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT OwnerId FROM Channels WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", channelId);
        var result = cmd.ExecuteScalar();
        return result == null ? null : Convert.ToInt32(result);
    }

    public bool IsChannelMember(string channelId, int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ChannelMembers WHERE ChannelId = @c AND UserId = @u";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@u", userId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public void AddChannelMemberToChannel(string channelId, int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO ChannelMembers (ChannelId, UserId, Role, JoinedAt)
            VALUES (@c, @u, 'Member', @time)";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public void RemoveChannelMemberFromChannel(string channelId, int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ChannelMembers WHERE ChannelId = @c AND UserId = @u";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.ExecuteNonQuery();
    }

    public List<string> GetUserChannelIds(int userId)
    {
        var ids = new List<string>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ChannelId FROM ChannelMembers WHERE UserId = @u";
        cmd.Parameters.AddWithValue("@u", userId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    public List<ChannelDto> GetUserChannels(int userId)
    {
        var list = new List<ChannelDto>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.Id, c.Name, c.OwnerId, COALESCE(u.UserName, 'User'),
                   (SELECT COUNT(*) FROM ChannelMembers m WHERE m.ChannelId = c.Id)
            FROM Channels c
            JOIN ChannelMembers m ON m.ChannelId = c.Id AND m.UserId = @u
            LEFT JOIN Clients u ON u.Id = c.OwnerId
            ORDER BY c.Name";
        cmd.Parameters.AddWithValue("@u", userId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChannelDto
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                OwnerId = reader.GetInt32(2),
                OwnerName = reader.GetString(3),
                MembersCount = (int)reader.GetInt64(4)
            });
        }
        return list;
    }

    public List<ChannelMemberDto> GetChannelMembers(string channelId)
    {
        var list = new List<ChannelMemberDto>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT m.UserId, COALESCE(c.UserName, 'User'), m.Role
            FROM ChannelMembers m
            LEFT JOIN Clients c ON c.Id = m.UserId
            WHERE m.ChannelId = @c
            ORDER BY m.JoinedAt";
        cmd.Parameters.AddWithValue("@c", channelId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChannelMemberDto
            {
                UserId = reader.GetInt32(0),
                UserName = reader.GetString(1),
                Role = reader.GetString(2)
            });
        }
        return list;
    }

    public void SaveChannelMessage(string messageId, string channelId, int senderId,
        string? nonce, string? ciphertext, string? tag,
        string contentType, string? fileName, long fileSize, string? fileTransferId,
        long chainIteration, string? replyToMessageId = null)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Messages
            (MessageId, SenderId, ReceiverId, ChannelId, Nonce, Ciphertext, Tag,
             ContentType, FileName, FileSize, FileTransferId, ChainIteration, Timestamp, Status, ReplyToMessageId)
            VALUES (@msgId, @sender, 0, @channel, @nonce, @ct, @tag,
                    @ctype, @fname, @fsize, @ftransfer, @iter, @time, 'Sent', @replyTo)
            ON CONFLICT(MessageId) DO NOTHING;";
        cmd.Parameters.AddWithValue("@msgId", messageId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@channel", channelId);
        cmd.Parameters.AddWithValue("@nonce", (object?)nonce ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ct", (object?)ciphertext ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", (object?)tag ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ctype", contentType);
        cmd.Parameters.AddWithValue("@fname", (object?)fileName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fsize", fileSize);
        cmd.Parameters.AddWithValue("@ftransfer", (object?)fileTransferId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@iter", chainIteration);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@replyTo", (object?)replyToMessageId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Страница истории канала: до limit сообщений старее курсора, по убыванию времени.</summary>
    public (List<ChannelMessageDto> messages, bool hasMore) GetChannelHistoryPage(
        string channelId, string? beforeTimestamp, int limit)
    {
        var list = new List<ChannelMessageDto>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, SenderId, ChainIteration, Nonce, Ciphertext, Tag,
                   ContentType, FileName, FileSize, Timestamp, DeletedAt
            FROM Messages
            WHERE ChannelId = @c AND (@before IS NULL OR Timestamp < @before)
            ORDER BY Timestamp DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@before", (object?)beforeTimestamp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", limit + 1); // +1 для признака HasMore
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (list.Count >= limit) continue; // лишнюю строку читаем только ради проверки HasMore
            list.Add(new ChannelMessageDto
            {
                MessageId = reader.GetString(0),
                SenderId = reader.GetInt32(1),
                ChainIteration = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                Nonce = reader.IsDBNull(3) ? null : reader.GetString(3),
                Ciphertext = reader.IsDBNull(4) ? null : reader.GetString(4),
                Tag = reader.IsDBNull(5) ? null : reader.GetString(5),
                ContentType = reader.GetString(6),
                FileName = reader.IsDBNull(7) ? null : reader.GetString(7),
                FileSize = reader.GetInt64(8),
                Timestamp = reader.GetString(9),
                IsDeleted = !reader.IsDBNull(10)
            });
        }
        bool hasMore = list.Count > 0 && HasOlderChannelMessages(conn, channelId, list[^1].Timestamp);
        return (list, hasMore);
    }

    private static bool HasOlderChannelMessages(SqliteConnection conn, string channelId, string oldestTimestamp)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE ChannelId = @c AND Timestamp < @t";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@t", oldestTimestamp);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    public void SaveChannelKey(string channelId, int userId, long version, string encryptedKey)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ChannelKeys (ChannelId, UserId, Version, EncryptedKey)
            VALUES (@c, @u, @v, @k)
            ON CONFLICT(ChannelId, UserId) DO UPDATE SET
                Version = excluded.Version, EncryptedKey = excluded.EncryptedKey";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@v", version);
        cmd.Parameters.AddWithValue("@k", encryptedKey);
        cmd.ExecuteNonQuery();
    }

    public (long version, string encryptedKey)? GetChannelKey(string channelId, int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Version, EncryptedKey FROM ChannelKeys WHERE ChannelId = @c AND UserId = @u";
        cmd.Parameters.AddWithValue("@c", channelId);
        cmd.Parameters.AddWithValue("@u", userId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetInt64(0), reader.GetString(1));
    }

    /// <summary>Удаление пользователя из всех каналов (при бане). Возвращает затронутые каналы.</summary>
    public List<string> DeleteUserFromAllChannels(int userId)
    {
        var affected = new List<string>();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        var select = conn.CreateCommand();
        select.CommandText = "SELECT ChannelId FROM ChannelMembers WHERE UserId = @u";
        select.Parameters.AddWithValue("@u", userId);
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read()) affected.Add(reader.GetString(0));
        }

        var delete = conn.CreateCommand();
        delete.CommandText = "DELETE FROM ChannelMembers WHERE UserId = @u";
        delete.Parameters.AddWithValue("@u", userId);
        delete.ExecuteNonQuery();

        return affected;
    }
}