using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class CacheCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await TestHostFactory.DisposeAsync(_provider);

    [Fact]
    public async Task SetNx_OnlyCreatesMissingKey()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "nx", "1", "NX"))
            .Should().Be(RespValue.Ok);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "nx", "2", "NX"))
            .IsNullBulk.Should().BeTrue();
        CommandTestHelper.GetBulkText(await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "nx"))
            .Should().Be("1");
    }

    [Fact]
    public async Task SetXx_OnlyUpdatesExistingKey()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "xx", "1", "XX"))
            .IsNullBulk.Should().BeTrue();
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "xx", "1");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "xx", "2", "XX"))
            .Should().Be(RespValue.Ok);
        CommandTestHelper.GetBulkText(await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "xx"))
            .Should().Be("2");
    }

    [Fact]
    public async Task SetGet_ReturnsPreviousValue()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "g", "old");
        var previous = await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "g", "new", "GET");
        CommandTestHelper.GetBulkText(previous).Should().Be("old");
        CommandTestHelper.GetBulkText(await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "g"))
            .Should().Be("new");
    }

    [Fact]
    public async Task Setex_AndPttl_RoundTrip()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SETEX", "ttl", "30", "v"))
            .Should().Be(RespValue.Ok);
        var pttl = await CommandTestHelper.ExecuteAsync(_provider, _connection, "PTTL", "ttl");
        pttl.Integer.Should().BeInRange(1, 30_000);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "TTL", "ttl")).Integer.Should().BeInRange(1, 30);
    }

    [Fact]
    public async Task Getex_SetsNewExpiry()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "ge", "v");
        var value = await CommandTestHelper.ExecuteAsync(_provider, _connection, "GETEX", "ge", "EX", "60");
        CommandTestHelper.GetBulkText(value).Should().Be("v");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "TTL", "ge")).Integer.Should().BeInRange(1, 60);
    }

    [Fact]
    public async Task Msetnx_Incrby_Getdel_Unlink_Strlen_Psetex()
    {
        (await CommandTestHelper.ExecuteAsync(
                _provider, _connection, "MSETNX", "a", "1", "b", "2")).Integer.Should().Be(1);
        (await CommandTestHelper.ExecuteAsync(
                _provider, _connection, "MSETNX", "a", "9", "c", "3")).Integer.Should().Be(0);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "INCRBY", "a", "5")).Integer.Should().Be(6);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "DECRBY", "a", "2")).Integer.Should().Be(4);

        var floatReply = await CommandTestHelper.ExecuteAsync(_provider, _connection, "INCRBYFLOAT", "f", "1.5");
        CommandTestHelper.GetBulkText(floatReply).Should().Be("1.5");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "PSETEX", "px", "5000", "payload"))
            .Should().Be(RespValue.Ok);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "STRLEN", "px")).Integer.Should().Be(7);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "PTTL", "px")).Integer.Should().BeInRange(1, 5000);

        CommandTestHelper.GetBulkText(
                await CommandTestHelper.ExecuteAsync(_provider, _connection, "GETDEL", "px"))
            .Should().Be("payload");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXISTS", "px")).Integer.Should().Be(0);

        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "u1", "x");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "u2", "y");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "UNLINK", "u1", "u2")).Integer.Should().Be(2);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "TOUCH", "missing")).Integer.Should().Be(0);
    }
}
