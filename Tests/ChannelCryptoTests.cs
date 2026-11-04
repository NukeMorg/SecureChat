using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

/// <summary>
/// Тесты групповой криптографии (Фаза C): Sender Keys ratchet, «догон»,
/// replay-защита, ротация ключа.
/// </summary>
public class ChannelCryptoTests
{
    private static byte[] Key(int seed)
    {
        var key = new byte[32];
        new Random(seed).NextBytes(key);
        return key;
    }

    [Fact]
    public void InitialChainKey_IsDeterministicPerSender()
    {
        var groupKey = Key(1);
        var a1 = ChannelCrypto.DeriveInitialChainKey(groupKey, 7);
        var a2 = ChannelCrypto.DeriveInitialChainKey(groupKey, 7);
        var b = ChannelCrypto.DeriveInitialChainKey(groupKey, 8);

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b); // разные отправители — разные цепочки
    }

    /// <summary>Пара «отправитель/получатель» с общим GroupKey (как в реальной системе).</summary>
    private static (ChannelCrypto Sender, ChannelCrypto Receiver) Pair(int seed)
    {
        byte[] groupKey = Key(seed);
        var sender = new ChannelCrypto();
        sender.SetGroupKey("ch1", groupKey);
        var receiver = new ChannelCrypto();
        receiver.SetGroupKey("ch1", groupKey);
        return (sender, receiver);
    }

    [Fact]
    public void SendThenReceive_RoundTripsMessageKey()
    {
        var (sender, receiver) = Pair(2);

        var (senderKey, iteration) = sender.GetNextMessageKey("ch1", senderId: 1)!.Value;
        var receiverKey = receiver.GetMessageKey("ch1", senderId: 1, iteration);

        Assert.NotNull(receiverKey);
        Assert.Equal(senderKey, receiverKey);
    }

    [Fact]
    public void MessageKeys_AdvancePerIteration()
    {
        var crypto = new ChannelCrypto();
        crypto.SetGroupKey("ch1", Key(3));

        var (k0, i0) = crypto.GetNextMessageKey("ch1", 5)!.Value;
        var (k1, i1) = crypto.GetNextMessageKey("ch1", 5)!.Value;

        Assert.Equal(0, i0);
        Assert.Equal(1, i1);
        Assert.NotEqual(k0, k1);
    }

    [Fact]
    public void Receiver_CatchesUpToArbitraryIteration()
    {
        var (sender, receiver) = Pair(4);

        byte[]? keyAt7 = null;
        for (int i = 0; i < 8; i++)
        {
            var (key, iteration) = sender.GetNextMessageKey("ch1", 3)!.Value;
            if (iteration == 7) keyAt7 = key;
        }

        // получатель «догоняет» с нуля до итерации 7
        var caught = receiver.GetMessageKey("ch1", senderId: 3, iteration: 7);
        Assert.Equal(keyAt7, caught);
    }

    [Fact]
    public void Replay_OlderIteration_IsRejected()
    {
        var crypto = new ChannelCrypto();
        crypto.SetGroupKey("ch1", Key(5));

        // получатель обрабатывает итерации 0..2 (курсор next = 3)
        for (long i = 0; i < 3; i++)
            Assert.NotNull(crypto.GetMessageKey("ch1", senderId: 9, iteration: i));

        // повтор старой итерации — replay, отвергается даже при наличии в кэше
        Assert.Null(crypto.GetMessageKey("ch1", senderId: 9, iteration: 0));
        Assert.Null(crypto.GetMessageKey("ch1", senderId: 9, iteration: 1));
        // следующее живое сообщение — валидно
        Assert.NotNull(crypto.GetMessageKey("ch1", senderId: 9, iteration: 3));
    }

    [Fact]
    public void MissingGroupKey_ReturnsNull()
    {
        var crypto = new ChannelCrypto();
        Assert.Null(crypto.GetNextMessageKey("unknown", 1));
        Assert.Null(crypto.GetMessageKey("unknown", 1, 0));
    }

    [Fact]
    public void Rotation_ResetsChainsAndBumpsVersion()
    {
        var crypto = new ChannelCrypto();
        crypto.SetGroupKey("ch1", Key(6));

        var (oldKey, _) = crypto.GetNextMessageKey("ch1", 2)!.Value;
        long v1 = crypto.GetKeyVersion("ch1");

        long v2 = crypto.SetGroupKey("ch1", Key(66), version: 5);

        Assert.Equal(5, v2);
        Assert.True(v2 > v1);
        // новая цепочка начинается с нуля и даёт другие ключи
        var (newKey, newIteration) = crypto.GetNextMessageKey("ch1", 2)!.Value;
        Assert.Equal(0, newIteration);
        Assert.NotEqual(oldKey, newKey);
    }

    [Fact]
    public void DeriveMessageKeyAt_MatchesLivePath()
    {
        var (sender, receiver) = Pair(7);

        byte[]? keyAt4 = null;
        for (int i = 0; i < 5; i++)
        {
            var (key, iteration) = sender.GetNextMessageKey("ch1", 4)!.Value;
            if (iteration == 4) keyAt4 = key;
        }

        // история: детерминированный вывод на любой итерации — без изменения состояния
        var at2 = receiver.DeriveMessageKeyAt("ch1", 4, 2);
        var at4 = receiver.DeriveMessageKeyAt("ch1", 4, 4);
        Assert.NotNull(at2);
        Assert.NotNull(at4);
        Assert.NotEqual(at2, at4);
        Assert.Equal(keyAt4, at4);
    }
}

/// <summary>Прямой AES-GCM round-trip (единый крипто-стек).</summary>
public class CryptoServiceTests
{
    [Fact]
    public void AesGcm_RoundTrip_ReturnsOriginalPlaintext()
    {
        var crypto = new CryptoService();
        byte[] key = crypto.GenerateSessionKey();
        byte[] plain = "Привет, SecureChat!"u8.ToArray();

        var (ct, nonce, tag) = crypto.AesGcmEncrypt(key, plain);
        byte[] decrypted = crypto.AesGcmDecrypt(key, nonce, ct, tag);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void AesGcm_TamperedCiphertext_Fails()
    {
        var crypto = new CryptoService();
        byte[] key = crypto.GenerateSessionKey();
        var (ct, nonce, tag) = crypto.AesGcmEncrypt(key, "secret"u8.ToArray());

        ct[0] ^= 0xFF;
        Assert.ThrowsAny<Exception>(() => crypto.AesGcmDecrypt(key, nonce, ct, tag));
    }

    [Fact]
    public void Rsa_RoundTrip_WithGeneratedPair()
    {
        var crypto = new CryptoService();
        var (publicPem, privatePem) = crypto.GenerateRsaKeyPair();
        byte[] data = new byte[32];
        Random.Shared.NextBytes(data);

        byte[] encrypted = crypto.RsaEncrypt(publicPem, data);
        byte[] decrypted = crypto.RsaDecrypt(privatePem, encrypted);

        Assert.Equal(data, decrypted);
    }
}
