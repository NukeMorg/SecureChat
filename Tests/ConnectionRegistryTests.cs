using SecureChat.Server;
using Xunit;

namespace SecureChat.Tests;

/// <summary>Тесты ConnectionRegistry: реестр userId → connectionId (вместо IUserIdProvider).</summary>
public class ConnectionRegistryTests
{
    [Fact]
    public void Register_GetUserId_ReturnsUser()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");

        Assert.Equal(1, registry.GetUserId("conn-A"));
        Assert.True(registry.IsOnline(1));
    }

    [Fact]
    public void GetUserId_UnknownConnection_ReturnsNull()
    {
        var registry = new ConnectionRegistry();
        Assert.Null(registry.GetUserId("no-such-conn"));
    }

    [Fact]
    public void Register_SameUserSecondConnection_BothConnectionsTracked()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");
        registry.Register(1, "conn-B");

        var connections = registry.GetConnections(1);
        Assert.Equal(2, connections.Count);
        Assert.Contains("conn-A", connections);
        Assert.Contains("conn-B", connections);
        Assert.True(registry.IsOnline(1));
    }

    [Fact]
    public void Unregister_LastConnection_UserGoesOffline()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");

        bool fullyOffline = registry.Unregister("conn-A", out int userId);

        Assert.True(fullyOffline);
        Assert.Equal(1, userId);
        Assert.False(registry.IsOnline(1));
        Assert.Empty(registry.GetConnections(1));
    }

    [Fact]
    public void Unregister_NotLastConnection_UserStaysOnline()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");
        registry.Register(1, "conn-B");

        bool fullyOffline = registry.Unregister("conn-A", out _);

        Assert.False(fullyOffline);
        Assert.True(registry.IsOnline(1));
        Assert.Equal(["conn-B"], registry.GetConnections(1));
    }

    [Fact]
    public void Unregister_UnknownConnection_ReturnsFalseAndZeroId()
    {
        var registry = new ConnectionRegistry();
        bool unregistered = registry.Unregister("ghost", out int userId);

        Assert.False(unregistered);
        Assert.Equal(0, userId);
    }

    [Fact]
    public void Register_SameConnectionReboundToAnotherUser_OldLinkRemoved()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");
        registry.Register(2, "conn-A"); // соединение переподключилось под другим пользователем

        Assert.Equal(2, registry.GetUserId("conn-A"));
        Assert.False(registry.IsOnline(1));
        Assert.True(registry.IsOnline(2));
        Assert.Equal(["conn-A"], registry.GetConnections(2));
    }

    [Fact]
    public void Register_SameUserSameConnectionAgain_IsIdempotent()
    {
        var registry = new ConnectionRegistry();
        registry.Register(1, "conn-A");
        registry.Register(1, "conn-A");

        Assert.Equal(["conn-A"], registry.GetConnections(1));
        Assert.True(registry.IsOnline(1));
    }

    [Fact]
    public async Task ConcurrentRegisterUnregister_DoesNotThrowAndConsistent()
    {
        var registry = new ConnectionRegistry();

        var tasks = Enumerable.Range(1, 8).Select(async worker =>
        {
            for (int i = 0; i < 200; i++)
            {
                int userId = worker;
                string conn = $"conn-{worker}-{i}";
                registry.Register(userId, conn);
                Assert.Equal(userId, registry.GetUserId(conn));
                registry.Unregister(conn, out _);
            }
            await Task.Yield();
        });

        await Task.WhenAll(tasks);

        for (int worker = 1; worker <= 8; worker++)
        {
            Assert.False(registry.IsOnline(worker));
        }
    }
}
