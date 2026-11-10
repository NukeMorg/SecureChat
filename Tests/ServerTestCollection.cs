using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Коллекция для тестов, меняющих Environment.CurrentDirectory
/// (строка подключения Database «Data Source=server.db» относительная).
/// DisableParallelization: CWD — состояние процесса, параллельно с другими
/// коллекциями его менять нельзя. Выполняются строго последовательно.
/// </summary>
[CollectionDefinition(nameof(ServerDatabaseCollection), DisableParallelization = true)]
public sealed class ServerDatabaseCollection;

/// <summary>
/// Фикстура на один тест: временная папка + переключение CWD на неё,
/// восстановление исходного CWD и удаление папки при Dispose.
/// </summary>
internal sealed class TempDbDirectory : IDisposable
{
    private readonly string _originalCwd = Environment.CurrentDirectory;
    private readonly string _path;

    public TempDbDirectory()
    {
        _path = Path.Combine(Path.GetTempPath(), "securechat-tests", "db-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_path);
        Environment.CurrentDirectory = _path;
    }

    /// <summary>Путь к временной папке (для прямого доступа к файлу БД в интеграционных тестах).</summary>
    public string DbFilePath => Path.Combine(_path, "server.db");
    public string ConnectionString => $"Data Source={DbFilePath};Default Timeout=10;";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Environment.CurrentDirectory = _originalCwd;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(_path))
                    Directory.Delete(_path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5) { Thread.Sleep(50); } // WAL-файлы могли не сразу освободиться
            catch (UnauthorizedAccessException) { return; }
        }
    }
}
