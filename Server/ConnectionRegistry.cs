using System.Collections.Concurrent;

namespace SecureChat.Server;

/// <summary>
/// Реестр связок userId → connectionId.
/// Аутентификация выполняется внутри хаба (Login/RegisterAccount/Hello),
/// поэтому стандартный IUserIdProvider неприменим — он разрешает пользователя
/// только в момент рукопожатия соединения.
/// </summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, byte>> _byUser = new();
    private readonly ConcurrentDictionary<string, int> _byConnection = new();

    public void Register(int userId, string connectionId)
    {
        // если соединение ранее было закреплено за другим пользователем — убираем старую связь
        if (_byConnection.TryGetValue(connectionId, out var oldUserId) && oldUserId != userId)
        {
            if (_byUser.TryGetValue(oldUserId, out var oldSet))
            {
                oldSet.TryRemove(connectionId, out _);
                if (oldSet.IsEmpty) _byUser.TryRemove(oldUserId, out _);
            }
        }

        var set = _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
        set[connectionId] = 0;
        _byConnection[connectionId] = userId;
    }

    /// <summary>Отвязывает соединение. Возвращает true, если пользователь остался полностью офлайн.</summary>
    public bool Unregister(string connectionId, out int userId)
    {
        userId = 0;
        if (!_byConnection.TryRemove(connectionId, out var uid)) return false;
        userId = uid;
        if (_byUser.TryGetValue(uid, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty) _byUser.TryRemove(uid, out _);
        }
        return true;
    }

    public int? GetUserId(string connectionId) =>
        _byConnection.TryGetValue(connectionId, out var uid) ? uid : null;

    public IReadOnlyList<string> GetConnections(int userId) =>
        _byUser.TryGetValue(userId, out var set) ? set.Keys.ToArray() : Array.Empty<string>();

    public bool IsOnline(int userId) => _byUser.ContainsKey(userId);
}
