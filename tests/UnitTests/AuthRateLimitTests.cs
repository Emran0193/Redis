using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class AuthRateLimitTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create(options =>
        {
            options.Password = "s3cret";
            options.AuthLockoutBase = TimeSpan.FromSeconds(2);
            options.AuthLockoutMax = TimeSpan.FromSeconds(10);
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await TestHostFactory.DisposeAsync(_provider);

    [Fact]
    public async Task FailedAuth_AppliesBackoff_ThenSucceedsWithCorrectPassword()
    {
        var connection = new FakeClientConnection { IsAuthenticated = false };

        var denied = await CommandTestHelper.ExecuteAsync(_provider, connection, "AUTH", "wrong");
        denied.Type.Should().Be(RespType.Error);
        denied.Simple.Should().Contain("invalid password");

        var locked = await CommandTestHelper.ExecuteAsync(_provider, connection, "AUTH", "wrong");
        locked.Type.Should().Be(RespType.Error);
        locked.Simple.Should().Contain("too many authentication failures");

        await Task.Delay(TimeSpan.FromSeconds(2.2));

        var ok = await CommandTestHelper.ExecuteAsync(_provider, connection, "AUTH", "s3cret");
        ok.Should().Be(RespValue.Ok);
        connection.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task HelloAuth_UsesSameBackoff()
    {
        var connection = new FakeClientConnection { IsAuthenticated = false };

        var denied = await CommandTestHelper.ExecuteAsync(
            _provider, connection, "HELLO", "2", "AUTH", "default", "nope");
        denied.Type.Should().Be(RespType.Error);
        denied.Simple.Should().Contain("invalid password");

        var locked = await CommandTestHelper.ExecuteAsync(
            _provider, connection, "HELLO", "2", "AUTH", "default", "nope");
        locked.Type.Should().Be(RespType.Error);
        locked.Simple.Should().Contain("too many authentication failures");
    }

    [Fact]
    public async Task FailedAuth_OnNewConnection_StillLockedByIp()
    {
        var first = new FakeClientConnection(remoteAddress: "10.0.0.9") { IsAuthenticated = false };
        (await CommandTestHelper.ExecuteAsync(_provider, first, "AUTH", "wrong"))
            .Type.Should().Be(RespType.Error);

        // Simulate disconnect clearing per-connection state only.
        await _provider.GetRequiredService<NovaDB.Networking.ICommandProcessor>()
            .OnDisconnectedAsync(first.ConnectionId, CancellationToken.None);

        var second = new FakeClientConnection(remoteAddress: "10.0.0.9") { IsAuthenticated = false };
        var locked = await CommandTestHelper.ExecuteAsync(_provider, second, "AUTH", "wrong");
        locked.Type.Should().Be(RespType.Error);
        locked.Simple.Should().Contain("too many authentication failures");
    }
}
