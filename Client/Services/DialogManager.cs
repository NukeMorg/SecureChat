using System.Collections.Generic;
using SecureChat.Core;

namespace SecureChat.Client.Services;

// Хранилище сессионных ключей и публичных ключей собеседников
public class DialogManager
{
    private readonly CryptoService _crypto;
    private readonly Dictionary<int, byte[]> _sessionKeys = new();         // contactId -> AES-сессионный ключ
    private readonly Dictionary<int, string> _publicKeys = new();          // contactId -> RSA-публичный ключ (PEM)

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
}