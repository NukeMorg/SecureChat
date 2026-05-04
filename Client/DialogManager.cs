using System.Collections.Generic;

namespace SecureChat.Client;

// Хранит сессионные ключи для каждого собеседника и историю сообщений
public class DialogManager
{
    private readonly CryptoService _crypto;
    // ключ: собеседник (int), значение: симметричный ключ (byte[])
    private Dictionary<int, byte[]> _sessionKeys = new();
    // история: ключ – собеседник, значение – список сообщений
    private Dictionary<int, List<Models.DialogMessage>> _history = new();

    public DialogManager(CryptoService crypto)
    {
        _crypto = crypto;
    }

    public byte[] GetOrCreateSessionKey(int contactId)
    {
        if (_sessionKeys.TryGetValue(contactId, out var key))
            return key;
        key = _crypto.GenerateSessionKey();
        _sessionKeys[contactId] = key;
        return key;
    }

    public byte[] GetSessionKey(int contactId)
    {
        _sessionKeys.TryGetValue(contactId, out var key);
        return key;
    }

    public void SetSessionKey(int contactId, byte[] key)
    {
        _sessionKeys[contactId] = key;
    }

    public void AddMessage(int contactId, Models.DialogMessage msg)
    {
        if (!_history.ContainsKey(contactId))
            _history[contactId] = new List<Models.DialogMessage>();
        _history[contactId].Add(msg);
    }

    public List<Models.DialogMessage> GetHistory(int contactId)
    {
        _history.TryGetValue(contactId, out var list);
        return list ?? new List<Models.DialogMessage>();
    }
}