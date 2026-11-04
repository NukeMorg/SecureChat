using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using SecureChat.Core;
using SecureChat.Security;
using Serilog;
using ILogger = Serilog.ILogger;

namespace SecureChat.Server;

public class Database
{
    readonly string cs = "Data Source=server.db;Default Timeout=10;";
    public string ConnectionString => cs;
    private static readonly ILogger Log = Serilog.Log.ForContext<Database>();

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
                UserName TEXT DEFAULT 'User'
            );
            CREATE TABLE IF NOT EXISTS EmailVerifications (
                Email TEXT PRIMARY KEY,
                Code TEXT NOT NULL,
                ExpiresAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                Token TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL,
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
            CREATE INDEX IF NOT EXISTS idx_email_rate ON EmailRateLimits(Email, LastSentAt);";
        cmd.ExecuteNonQuery();
        Log.Information("База данных инициализирована: {DbPath}", Path.GetFullPath("server.db"));
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

    public enum CodeVerifyResult { Valid, Invalid, Expired }

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
        DateTime expiresAt = DateTime.Parse(reader.GetString(1));

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

    public static (string hash, string salt) HashPassword(string password)
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

    public (int id, string token)? RegisterUser(string email, string password, string userName, string publicKey)
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
        string token = CreateSessionInternal(conn, id);
        Log.Debug("RegisterUser создан userId={UserId}", id);
        return (id, token);
    }

    public (int id, string name, string token)? Authenticate(string email, string password)
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
                string token = CreateSessionInternal(conn, id);
                Log.Debug("Authenticate successful for userId={UserId}", id);
                return (id, name, token);
            }
        }
        return null;
    }

    private string CreateSessionInternal(SqliteConnection conn, int userId)
    {
        string token = Guid.NewGuid().ToString("N");
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Sessions (Token, UserId, CreatedAt) VALUES (@token, @uid, @time)";
        cmd.Parameters.AddWithValue("@token", token);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return token;
    }

    public string CreateSession(int userId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        return CreateSessionInternal(conn, userId);
    }

    public int? ValidateSession(string token)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserId FROM Sessions WHERE Token = @token";
        cmd.Parameters.AddWithValue("@token", token);
        var result = cmd.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : null;
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
                            string status = "Sent", string? encryptedKeySelf = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Messages
            (MessageId, SenderId, ReceiverId, EncryptedKey, Nonce, Ciphertext, Tag,
             ContentType, FileName, FileSize, FileTransferId, Timestamp, Status, EncryptedKeySelf)
            VALUES (@msgId, @sender, @receiver, @encKey, @nonce, @ct, @tag,
                    @ctype, @fname, @fsize, @ftransfer, @time, @status, @encKeySelf)
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
        cmd.ExecuteNonQuery();
        sw.Stop();
        if (sw.ElapsedMilliseconds > 100)
            Log.Warning("SaveMessage медленно: {DurationMs}ms messageId={MessageId}", sw.ElapsedMilliseconds, messageId);
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

    public List<HistoryMessage> GetHistory(int userId1, int userId2)
    {
        var list = new List<HistoryMessage>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MessageId, SenderId, ReceiverId, EncryptedKey, EncryptedKeySelf, Nonce, Ciphertext, Tag,
                   ContentType, FileName, FileSize, FileTransferId, Timestamp, Status
            FROM Messages
            WHERE (SenderId = @user1 AND ReceiverId = @user2)
               OR (SenderId = @user2 AND ReceiverId = @user1)
            ORDER BY Timestamp ASC";
        cmd.Parameters.AddWithValue("@user1", userId1);
        cmd.Parameters.AddWithValue("@user2", userId2);
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
        Log.Debug("GetHistory user1={U1} user2={U2} count={Count}", userId1, userId2, list.Count);
        return list;
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
}