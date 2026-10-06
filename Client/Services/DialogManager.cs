using System.Collections.Generic;

namespace SecureChat.Client.Services;

// Хранит сессионные ключи для каждого собеседника и историю сообщений.
public class DialogManager
{
    private readonly CryptoService _crypto;
    private Dictionary<int, byte[]> _sessionKeys = new();
    private Dictionary<int, List<Models.DialogMessage>> _history = new();

    public DialogManager(CryptoService crypto)
    {
        _crypto = crypto;
    }

    // Возвращает существующий сессионный ключ или создаёт новый.
    public byte[] GetOrCreateSessionKey(int contactId)
    {
        if (_sessionKeys.TryGetValue(contactId, out var key))
            return key;
        key = _crypto.GenerateSessionKey();
        _sessionKeys[contactId] = key;
        return key;
    }

    // Получить сессионный ключ (может быть null).
    public byte[]? GetSessionKey(int contactId)
    {
        _sessionKeys.TryGetValue(contactId, out var key);
        return key;
    }

    // Задать сессионный ключ.
    public void SetSessionKey(int contactId, byte[] key)
    {
        _sessionKeys[contactId] = key;
    }

    // Добавить сообщение в историю.
    public void AddMessage(int contactId, Models.DialogMessage msg)
    {
        if (!_history.ContainsKey(contactId))
            _history[contactId] = new List<Models.DialogMessage>();
        _history[contactId].Add(msg);
    }

    // Получить историю сообщений с контактом.
    public List<Models.DialogMessage> GetHistory(int contactId)
    {
        _history.TryGetValue(contactId, out var list);
        return list ?? new List<Models.DialogMessage>();
    }
}