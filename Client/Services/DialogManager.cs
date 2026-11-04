using System.Collections.Generic;
using SecureChat.Core;

namespace SecureChat.Client.Services;

// Хранилище сессионных ключей, публичных ключей и локальной истории диалогов
public class DialogManager
{
    private readonly CryptoService _crypto;
    private Dictionary<int, byte[]> _sessionKeys = new();                  // contactId -> AES-сессионный ключ
    private Dictionary<int, List<Models.DialogMessage>> _history = new(); // contactId -> локальная история
    private Dictionary<int, string> _publicKeys = new();                   // contactId -> RSA-публичный ключ (PEM)

    public DialogManager(CryptoService crypto)
    {
        _crypto = crypto;
    }

    // Вернуть существующий сессионный ключ или сгенерировать новый
    public byte[] GetOrCreateSessionKey(int contactId)
    {
        if (_sessionKeys.TryGetValue(contactId, out var key))
            return key;
        key = _crypto.GenerateSessionKey();
        _sessionKeys[contactId] = key;
        return key;
    }

    // Получить сессионный ключ или null, если ещё не создан
    public byte[]? GetSessionKey(int contactId)
    {
        _sessionKeys.TryGetValue(contactId, out var key);
        return key;
    }

    public void SetSessionKey(int contactId, byte[] key)
    {
        _sessionKeys[contactId] = key;
    }

    // Публичный ключ собеседника (кэш, чтобы не запрашивать каждый раз)
    public string? GetPublicKey(int contactId)
    {
        _publicKeys.TryGetValue(contactId, out var key);
        return key;
    }

    public void SetPublicKey(int contactId, string key)
    {
        _publicKeys[contactId] = key;
    }

    // Добавление сообщения в локальную историю диалога
    public void AddMessage(int contactId, Models.DialogMessage msg)
    {
        if (!_history.ContainsKey(contactId))
            _history[contactId] = new List<Models.DialogMessage>();
        _history[contactId].Add(msg);
    }

    // Получение локальной истории (пустой список, если нет данных)
    public List<Models.DialogMessage> GetHistory(int contactId)
    {
        _history.TryGetValue(contactId, out var list);
        return list ?? new List<Models.DialogMessage>();
    }
}