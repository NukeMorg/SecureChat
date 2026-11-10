using SecureChat.Client.Models;
using SecureChat.Client.Services;
using SecureChat.Core;
using Xunit;

namespace SecureChat.Tests;

public class DialogManagerTests
{
    private readonly DialogManager _manager = new(new CryptoService());

    [Fact]
    public void GetOrCreateSessionKey_GeneratesAndCachesKey()
    {
        byte[] key1 = _manager.GetOrCreateSessionKey(101);
        Assert.NotNull(key1);
        Assert.Equal(32, key1.Length);

        byte[] key2 = _manager.GetOrCreateSessionKey(101);
        Assert.Same(key1, key2);
    }

    [Fact]
    public void GetOrCreateSessionKey_DifferentContacts_DifferentKeys()
    {
        byte[] key1 = _manager.GetOrCreateSessionKey(1);
        byte[] key2 = _manager.GetOrCreateSessionKey(2);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void GetSessionKey_ReturnsNullWhenNotSet()
    {
        Assert.Null(_manager.GetSessionKey(999));
    }

    [Fact]
    public void SetSessionKey_OverridesOrStoresKey()
    {
        byte[] customKey = new byte[32];
        customKey[0] = 0x42;

        _manager.SetSessionKey(200, customKey);
        Assert.Same(customKey, _manager.GetSessionKey(200));
        Assert.Same(customKey, _manager.GetOrCreateSessionKey(200));
    }

    [Fact]
    public void PublicKey_GetAndSet()
    {
        Assert.Null(_manager.GetPublicKey(10));

        string pem = "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8A\n-----END PUBLIC KEY-----";
        _manager.SetPublicKey(10, pem);

        Assert.Equal(pem, _manager.GetPublicKey(10));
        Assert.Null(_manager.GetPublicKey(20));
    }

    [Fact]
    public void AddMessage_And_GetHistory_IsIsolatedPerContact()
    {
        Assert.Empty(_manager.GetHistory(50));

        var msg1 = new DialogMessage { Id = "m1", Text = "Hello", SenderId = 1, ReceiverId = 50, ContentType = "Text" };
        var msg2 = new DialogMessage { Id = "m2", Text = "World", SenderId = 50, ReceiverId = 1, ContentType = "Text" };

        _manager.AddMessage(50, msg1);
        _manager.AddMessage(50, msg2);

        var history50 = _manager.GetHistory(50);
        Assert.Equal(2, history50.Count);
        Assert.Equal("m1", history50[0].Id);
        Assert.Equal("m2", history50[1].Id);

        Assert.Empty(_manager.GetHistory(60));
    }
}
