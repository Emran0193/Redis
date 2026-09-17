using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class HashCacheCommandTests : IAsyncLifetime
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
    public async Task Hash_Hmget_Hincrby_Exists_Len_Keys_Vals()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "HSET", "h", "f1", "10");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "HSET", "h", "f2", "x");

        var hmget = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HMGET", "h", "f1", "missing", "f2");
        hmget.Array!.Should().HaveCount(3);
        CommandTestHelper.GetBulkText(hmget.Array[0]).Should().Be("10");
        hmget.Array[1].IsNullBulk.Should().BeTrue();
        CommandTestHelper.GetBulkText(hmget.Array[2]).Should().Be("x");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HINCRBY", "h", "f1", "5"))
            .Integer.Should().Be(15);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HEXISTS", "h", "f1")).Integer.Should().Be(1);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HEXISTS", "h", "no")).Integer.Should().Be(0);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "HLEN", "h")).Integer.Should().Be(2);

        var keys = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HKEYS", "h");
        keys.Array!.Select(x => x.AsUtf8String()).Should().BeEquivalentTo("f1", "f2");

        var vals = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HVALS", "h");
        vals.Array!.Select(x => x.AsUtf8String()).Should().BeEquivalentTo("15", "x");
    }
}
