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
