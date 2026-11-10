using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Core;

/// <summary>
/// Групповая криптография каналов.
/// GroupKey (32 байта CSPRNG) существует только здесь и у других участников;
/// сервер видит лишь шифртекст и зашифрованные RSA-копии ключа.
///
/// Sender Keys ratchet на HMAC-SHA256:
///   ChainKey0(sender) = HMAC-SHA256(GroupKey, "chain" || senderId)
///   MessageKey(i)     = HMAC-SHA256(ChainKey(i), 0x02)
///   ChainKey(i+1)     = HMAC-SHA256(ChainKey(i), 0x01)
///
/// Шифрование сообщений — существующий AesGcmEncrypt с MessageKey.
/// Приём: «догон» цепочки отправителя до ChainIteration с кэшированием промежуточных
/// ключей; повтор живого сообщения (iteration ниже обработанной) отклоняется —
/// replay-защита. Ротация GroupKey сбрасывает все цепочки канала.
/// </summary>
public class ChannelCrypto
{
    private sealed class RatchetState
    {
        public byte[] ChainKey = Array.Empty<byte>(); // ключ цепочки для СЛЕДУЮЩЕЙ итерации
        public long NextIteration;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, byte[]> _groupKeys = new();
    private readonly Dictionary<string, long> _keyVersions = new();
    private readonly Dictionary<(string ChannelId, int SenderId), RatchetState> _ratchets = new();
    private readonly Dictionary<(string ChannelId, int SenderId, long Iteration), byte[]> _messageKeys = new();

    private const long MaxCatchUpIterations = 10_000; // защита от DoS «догоном»
    private const int MaxCachedMessageKeys = 20_000;

    // ---------- Ключ канала ----------

    /// <summary>
    /// Сохранить GroupKey канала. Возвращает эффективную версию: переданную, если она выше
    /// текущей, иначе текущая + 1. Все ratchet-цепочки канала сбрасываются.
    /// </summary>
    public long SetGroupKey(string channelId, byte[] groupKey, long version = -1)
    {
        lock (_lock)
        {
            long current = _keyVersions.TryGetValue(channelId, out var v) ? v : -1;
            long effective = version > current ? version : current + 1;
            _groupKeys[channelId] = (byte[])groupKey.Clone();
            _keyVersions[channelId] = effective;

            foreach (var key in _ratchets.Keys.Where(k => k.ChannelId == channelId).ToList())
                _ratchets.Remove(key);
            foreach (var key in _messageKeys.Keys.Where(k => k.ChannelId == channelId).ToList())
                _messageKeys.Remove(key);

            return effective;
        }
    }

    /// <summary>Текущая версия ключа канала; -1 — ключ неизвестен.</summary>
    public long GetKeyVersion(string channelId)
    {
        lock (_lock)
        {
            return _keyVersions.TryGetValue(channelId, out var v) ? v : -1;
        }
    }

    public bool HasGroupKey(string channelId)
    {
        lock (_lock)
        {
            return _groupKeys.ContainsKey(channelId);
        }
    }

    /// <summary>Открытый GroupKey — только для шифрования delivery другим участникам.</summary>
    public byte[]? GetGroupKey(string channelId)
    {
        lock (_lock)
        {
            return _groupKeys.TryGetValue(channelId, out var key) ? (byte[])key.Clone() : null;
        }
    }

    // ---------- Отправка ----------

    /// <summary>Следующий MessageKey отправителя + номер итерации для поля ChainIteration.</summary>
    public (byte[] MessageKey, long Iteration)? GetNextMessageKey(string channelId, int senderId)
    {
        lock (_lock)
        {
            if (!_groupKeys.TryGetValue(channelId, out var groupKey)) return null;

            var state = GetOrInitRatchet(channelId, senderId, groupKey);
            long iteration = state.NextIteration;
            byte[] messageKey = DeriveMessageKey(state.ChainKey);

            _messageKeys[(channelId, senderId, iteration)] = (byte[])messageKey.Clone();
            TrimMessageKeyCache();

            state.ChainKey = AdvanceChain(state.ChainKey);
            state.NextIteration = iteration + 1;
            return (messageKey, iteration);
        }
    }

    // ---------- Приём ----------

    /// <summary>
    /// MessageKey для живого сообщения: «догон» цепочки отправителя до iteration.
    /// null — ключ канала отсутствует, replay (iteration ниже обработанной)
    /// или аномальный скачок вперёд.
    /// </summary>
    public byte[]? GetMessageKey(string channelId, int senderId, long iteration)
    {
        lock (_lock)
        {
            if (!_groupKeys.TryGetValue(channelId, out var groupKey)) return null;

            // replay-защита строго по курсору: итерация позади обработанной — reject
            var state = GetOrInitRatchet(channelId, senderId, groupKey);
            if (iteration < state.NextIteration) return null;
            if (iteration - state.NextIteration > MaxCatchUpIterations) return null;
            if (_messageKeys.TryGetValue((channelId, senderId, iteration), out var cached)) return cached;

            CatchUp(channelId, senderId, state, iteration);

            byte[] messageKey = DeriveMessageKey(state.ChainKey);
            _messageKeys[(channelId, senderId, iteration)] = (byte[])messageKey.Clone();
            TrimMessageKeyCache();

            state.ChainKey = AdvanceChain(state.ChainKey);
            state.NextIteration = iteration + 1;
            return messageKey;
        }
    }

    /// <summary>
    /// Детерминированный вывод MessageKey для итерации без изменения состояния —
    /// для расшифровки истории (страницы могут содержать «старые» итерации).
    /// </summary>
    public byte[]? DeriveMessageKeyAt(string channelId, int senderId, long iteration)
    {
        lock (_lock)
        {
            if (!_groupKeys.TryGetValue(channelId, out var groupKey)) return null;
            if (iteration < 0 || iteration > MaxCatchUpIterations) return null;

            byte[] chainKey = DeriveInitialChainKey(groupKey, senderId);
            for (long i = 0; i < iteration; i++)
                chainKey = AdvanceChain(chainKey);
            return DeriveMessageKey(chainKey);
        }
    }

    // ---------- Внутреннее ----------

    private void CatchUp(string channelId, int senderId, RatchetState state, long targetIteration)
    {
        while (state.NextIteration < targetIteration)
        {
            _messageKeys[(channelId, senderId, state.NextIteration)] = DeriveMessageKey(state.ChainKey);
            state.ChainKey = AdvanceChain(state.ChainKey);
            state.NextIteration++;
        }
    }

    private RatchetState GetOrInitRatchet(string channelId, int senderId, byte[] groupKey)
    {
        if (_ratchets.TryGetValue((channelId, senderId), out var state)) return state;
        state = new RatchetState
        {
            ChainKey = DeriveInitialChainKey(groupKey, senderId),
            NextIteration = 0
        };
        _ratchets[(channelId, senderId)] = state;
        return state;
    }

    /// <summary>Начальный ChainKey отправителя: HMAC-SHA256(GroupKey, "chain"||SenderId) — public для тестов KDF.</summary>
    public static byte[] DeriveInitialChainKey(byte[] groupKey, int senderId)
    {
        using var hmac = new HMACSHA256(groupKey);
        byte[] label = Encoding.UTF8.GetBytes("chain" + senderId.ToString(CultureInfo.InvariantCulture));
        return hmac.ComputeHash(label);
    }

    internal static byte[] DeriveMessageKey(byte[] chainKey)
    {
        using var hmac = new HMACSHA256(chainKey);
        return hmac.ComputeHash(new byte[] { 0x02 });
    }

    internal static byte[] AdvanceChain(byte[] chainKey)
    {
        using var hmac = new HMACSHA256(chainKey);
        return hmac.ComputeHash(new byte[] { 0x01 });
    }

    private void TrimMessageKeyCache()
    {
        if (_messageKeys.Count <= MaxCachedMessageKeys) return;
        foreach (var key in _messageKeys.Keys.Take(_messageKeys.Count - MaxCachedMessageKeys).ToList())
            _messageKeys.Remove(key);
    }
}
