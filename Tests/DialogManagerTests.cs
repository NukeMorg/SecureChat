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
}
