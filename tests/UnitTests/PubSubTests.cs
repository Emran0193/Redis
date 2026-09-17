using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class PubSubTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _subscriber = null!;
    private FakeClientConnection _publisher = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _subscriber = new FakeClientConnection("subscriber");
        _publisher = new FakeClientConnection("publisher");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task SubscribePublish_FansOutMessageToSubscriber()
    {
        var subscribe = await CommandTestHelper.ExecuteAsync(_provider, _subscriber, "SUBSCRIBE", "news");

        subscribe.Type.Should().Be(RespType.Array);
        subscribe.Array![0].AsUtf8String().Should().Be("subscribe");
        subscribe.Array[1].AsUtf8String().Should().Be("news");

        var receivers = await CommandTestHelper.ExecuteAsync(_provider, _publisher, "PUBLISH", "news", "hello");
        receivers.Integer.Should().Be(1);

        await WaitForPushesAsync(_subscriber, expected: 1);

        _subscriber.PushedMessages.Should().HaveCount(1);
        var message = _subscriber.PushedMessages[0];
        message.Type.Should().Be(RespType.Array);
        message.Array![0].AsUtf8String().Should().Be("message");
        message.Array[1].AsUtf8String().Should().Be("news");
        message.Array[2].AsUtf8String().Should().Be("hello");
    }

    [Fact]
    public async Task Publish_WithNoSubscribers_ReturnsZero()
    {
        var receivers = await CommandTestHelper.ExecuteAsync(_provider, _publisher, "PUBLISH", "empty", "msg");
        receivers.Integer.Should().Be(0);
    }

    private static async Task WaitForPushesAsync(FakeClientConnection connection, int expected)
    {
        for (var i = 0; i < 100 && connection.PushedMessages.Count < expected; i++)
        {
            await Task.Delay(10);
        }
    }
}
