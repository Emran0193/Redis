using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class ExpirationTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task Expire_SetsTtlAndPersistRemovesIt()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "ttl-key", "value");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXPIRE", "ttl-key", "60"))
            .Integer.Should().Be(1);

        var ttl = await CommandTestHelper.ExecuteAsync(_provider, _connection, "TTL", "ttl-key");
        ttl.Integer.Should().BeInRange(1, 60);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "PERSIST", "ttl-key"))
            .Integer.Should().Be(1);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "TTL", "ttl-key"))
            .Integer.Should().Be(-1);
    }

    [Fact]
    public async Task Expire_OnMissingKey_ReturnsZero()
    {
        var result = await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXPIRE", "missing", "10");
        result.Integer.Should().Be(0);
    }

    [Fact]
    public async Task Ttl_OnMissingKey_ReturnsMinusTwo()
    {
        var ttl = await CommandTestHelper.ExecuteAsync(_provider, _connection, "TTL", "missing");
        ttl.Integer.Should().Be(-2);
    }

    [Fact]
    public async Task ExpireAfterOneSecond_KeyIsRemovedByTimingWheel()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "expiring", "gone");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXPIRE", "expiring", "1"))
            .Integer.Should().Be(1);

        await Task.Delay(1100);

        var get = await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "expiring");
        get.IsNullBulk.Should().BeTrue();
    }
}
