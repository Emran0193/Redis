using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Commands;
using NovaDB.Commands.PubSub;
using NovaDB.Networking;
using NovaDB.Protocol;
using NovaDB.PubSub;
using UnitTests.Helpers;

namespace UnitTests;

/// <summary>
/// Ensures disconnect tears down session and pub/sub state instead of leaking forever.
/// </summary>
public sealed class DisconnectCleanupTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task OnDisconnected_RemovesSessionAndSubscriptions()
    {
        var processor = _provider.GetRequiredService<ICommandProcessor>();
        var cache = _provider.GetRequiredService<PubSubSubscriberCache>();
        var hub = _provider.GetRequiredService<IPubSubHub>();
        var connection = new FakeClientConnection();

        await CommandTestHelper.ExecuteAsync(_provider, connection, "SUBSCRIBE", "news");
        cache.GetOrAdd(connection).ConnectionId.Should().Be(connection.ConnectionId);

        (await hub.PublishAsync("news", "ping"u8.ToArray(), CancellationToken.None)).Should().Be(1);

        // The subscriber pump is asynchronous; wait briefly for the push to land.
        for (var i = 0; i < 50 && connection.PushedMessages.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        connection.PushedMessages.Should().NotBeEmpty();

        await processor.OnDisconnectedAsync(connection.ConnectionId, CancellationToken.None);

        cache.TryRemove(connection.ConnectionId).Should().BeNull("cache entry must already be gone");
        (await hub.PublishAsync("news", "again"u8.ToArray(), CancellationToken.None)).Should().Be(0);

        // A fresh session should be created if the same id reconnects; the old MULTI/auth state is gone.
        connection.IsAuthenticated = false;
        var ping = await CommandTestHelper.ExecuteAsync(_provider, connection, "PING");
        ping.Should().Be(RespValue.Pong);
    }

    [Fact]
    public async Task Quit_SetsShouldCloseOnConnection()
    {
        var connection = new FakeClientConnection();

        var response = await CommandTestHelper.ExecuteAsync(_provider, connection, "QUIT");

        response.Should().Be(RespValue.Ok);
        connection.ShouldClose.Should().BeTrue();
    }
}
