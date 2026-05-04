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
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS Clients (Id INTEGER PRIMARY KEY AUTOINCREMENT, PublicKey TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
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

    public List<int> GetAllUserIds()
    {
        var list = new List<int>();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Clients";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetInt32(0));
        return list;
    }
}