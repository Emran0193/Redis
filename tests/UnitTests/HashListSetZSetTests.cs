using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class HashListSetZSetTests : IAsyncLifetime
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
    public async Task HashCommands_StoreAndRetrieveFields()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HSET", "hash", "f1", "v1"))
            .Integer.Should().Be(1);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HSET", "hash", "f2", "v2"))
            .Integer.Should().Be(1);

        CommandTestHelper.GetBulkText(
            await CommandTestHelper.ExecuteAsync(_provider, _connection, "HGET", "hash", "f1"))
            .Should().Be("v1");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HDEL", "hash", "f2"))
            .Integer.Should().Be(1);

        var all = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HGETALL", "hash");
        all.Type.Should().Be(RespType.Array);
        all.Array!.Should().HaveCount(2);
        all.Array[0].AsUtf8String().Should().Be("f1");
        all.Array[1].AsUtf8String().Should().Be("v1");
    }

    [Fact]
    public async Task ListCommands_PushPopAndRange()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "LPUSH", "list", "a", "b"))
            .Integer.Should().Be(2);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "RPUSH", "list", "c"))
            .Integer.Should().Be(3);

        CommandTestHelper.GetBulkText(
            await CommandTestHelper.ExecuteAsync(_provider, _connection, "LPOP", "list"))
            .Should().Be("b");
        CommandTestHelper.GetBulkText(
            await CommandTestHelper.ExecuteAsync(_provider, _connection, "RPOP", "list"))
            .Should().Be("c");

        var range = await CommandTestHelper.ExecuteAsync(_provider, _connection, "LRANGE", "list", "0", "-1");
        range.Array!.Should().HaveCount(1);
        range.Array[0].AsUtf8String().Should().Be("a");
    }

    [Fact]
    public async Task SetCommands_AddRemoveAndMembers()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SADD", "set", "x", "y", "x"))
            .Integer.Should().Be(2);

        var members = await CommandTestHelper.ExecuteAsync(_provider, _connection, "SMEMBERS", "set");
        members.Array!.Select(v => v.AsUtf8String()).Should().BeEquivalentTo(["x", "y"]);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SREM", "set", "x"))
            .Integer.Should().Be(1);
    }

    [Fact]
    public async Task SortedSetCommands_AddRangeScoreAndRange()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "ZADD", "zset", "1", "one", "2", "two"))
            .Integer.Should().Be(2);

        CommandTestHelper.GetBulkText(
            await CommandTestHelper.ExecuteAsync(_provider, _connection, "ZSCORE", "zset", "two"))
            .Should().Be("2");

        var range = await CommandTestHelper.ExecuteAsync(_provider, _connection, "ZRANGE", "zset", "0", "-1");
        range.Array!.Select(v => v.AsUtf8String()).Should().ContainInOrder("one", "two");
    }
}
