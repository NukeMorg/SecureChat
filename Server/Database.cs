using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using SecureChat.Common;

namespace SecureChat.Server;

// Работа с базой данных SQLite: хранение клиентов, сообщений, контактов.
public class Database
{
    readonly string cs = "Data Source=server.db";

    public string ConnectionString => cs;

    // Инициализирует таблицы и выполняет необходимые миграции.
    public void Initialize()
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        // Создаём таблицы, если их ещё нет
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Clients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PublicKey TEXT NOT NULL,
                UserName TEXT DEFAULT 'User'
            );
            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId TEXT UNIQUE NOT NULL,
                SenderId INTEGER NOT NULL,
                ReceiverId INTEGER NOT NULL,
                EncryptedKey TEXT,
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
            );";
        cmd.ExecuteNonQuery();

        // Миграция: добавляем столбец EncryptedKeySelf, если его ещё нет
        try
        {
            cmd.CommandText = "ALTER TABLE Messages ADD COLUMN EncryptedKeySelf TEXT";
            cmd.ExecuteNonQuery();
        }
        catch { /* столбец уже существует */ }

        // Миграция для старых таблиц (имя пользователя)
        try
        {
            cmd.CommandText = "ALTER TABLE Clients ADD COLUMN UserName TEXT DEFAULT 'User'";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // Добавляет нового пользователя с указанным публичным ключом и возвращает его ID.
    public int AddUser(string pubKey)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Clients (PublicKey) VALUES (@key); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@key", pubKey);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // Проверяет, существует ли пользователь с заданным ID.
    public bool UserExists(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Clients WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return (long)cmd.ExecuteScalar() > 0;
    }

    // Возвращает публичный ключ пользователя по ID (или null).
    public string? GetPublicKey(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PublicKey FROM Clients WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Обновляет имя пользователя.
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

    // Получает имя пользователя по ID.
    public string? GetUserName(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT UserName FROM Clients WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteScalar() as string;
    }

    // Возвращает список всех зарегистрированных пользователей (ID и имя).
    public List<(int id, string name)> GetAllUsers()
    {
        var list = new List<(int, string)>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, UserName FROM Clients";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetInt32(0), reader.GetString(1)));
        return list;
    }

    // Сохраняет сообщение в БД.
    // Параметр encryptedKeySelf добавлен для возможности самому расшифровывать свою историю.
    public void SaveMessage(string messageId, int senderId, int receiverId,
                            string? encryptedKey, string? nonce, string? ciphertext, string? tag,
                            string contentType, string? fileName, long fileSize, string? fileTransferId,
                            string status = "Sent", string? encryptedKeySelf = null) // === ИЗМЕНЕНИЕ (добавлен параметр) ===
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO Messages
            (MessageId, SenderId, ReceiverId, EncryptedKey, Nonce, Ciphertext, Tag,
             ContentType, FileName, FileSize, FileTransferId, Timestamp, Status, EncryptedKeySelf)
            VALUES (@msgId, @sender, @receiver, @encKey, @nonce, @ct, @tag,
                    @ctype, @fname, @fsize, @ftransfer, @time, @status, @encKeySelf)";
        cmd.Parameters.AddWithValue("@msgId", messageId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@receiver", receiverId);
        cmd.Parameters.AddWithValue("@encKey", encryptedKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@nonce", nonce ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ct", ciphertext ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@tag", tag ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@ctype", contentType);
        cmd.Parameters.AddWithValue("@fname", fileName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@fsize", fileSize);
        cmd.Parameters.AddWithValue("@ftransfer", fileTransferId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@time", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@encKeySelf", encryptedKeySelf ?? (object)DBNull.Value); // === ИЗМЕНЕНИЕ ===
        cmd.ExecuteNonQuery();
    }

    // Обновляет статус доставки сообщения.
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

    // Возвращает историю сообщений между двумя пользователями.
    // В выборку добавлен EncryptedKeySelf.
    public List<HistoryMessage> GetHistory(int userId1, int userId2)
    {
        var list = new List<HistoryMessage>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        // === ИЗМЕНЕНИЕ: добавили EncryptedKeySelf в SELECT ===
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
                EncryptedKeySelf = reader.IsDBNull(4) ? null : reader.GetString(4), // === ИЗМЕНЕНИЕ ===
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

    // Добавляет двусторонний контакт (userId <-> contactId).
    public void AddContact(int userId, int contactId)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO Contacts (UserId, ContactId) VALUES (@uid, @cid)";
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@cid", contactId);
        cmd.ExecuteNonQuery();
        // двусторонняя связь
        cmd.CommandText = "INSERT OR IGNORE INTO Contacts (UserId, ContactId) VALUES (@uid2, @cid2)";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@uid2", contactId);
        cmd.Parameters.AddWithValue("@cid2", userId);
        cmd.ExecuteNonQuery();
    }

    // Возвращает список ID контактов для указанного пользователя.
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