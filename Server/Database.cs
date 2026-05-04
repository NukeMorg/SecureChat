using Microsoft.Data.Sqlite;

namespace SecureChat.Server;

public class Database
{
    readonly string cs = "Data Source=server.db";

    public void Initialize()
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS Clients (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            PublicKey TEXT NOT NULL,
            UserName TEXT DEFAULT 'User')";
        cmd.ExecuteNonQuery();
        // добавляем столбец, если таблица существовала ранее
        try
        {
            cmd.CommandText = "ALTER TABLE Clients ADD COLUMN UserName TEXT DEFAULT 'User'";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public int AddUser(string pubKey)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Clients (PublicKey) VALUES (@key); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@key", pubKey);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool UserExists(int id)
    {
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Clients WHERE Id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        return (long)cmd.ExecuteScalar() > 0;
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
}