using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class StringCommandTests : IAsyncLifetime
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
    public async Task SetAndGet_RoundTripValue()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "name", "NovaDB"))
            .Should().Be(RespValue.Ok);

        var get = await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "name");

        CommandTestHelper.GetBulkText(get).Should().Be("NovaDB");
    }

    [Fact]
    public async Task MsetAndMget_ReturnAllValues()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "MSET", "a", "1", "b", "2"))
            .Should().Be(RespValue.Ok);

        var response = await CommandTestHelper.ExecuteAsync(_provider, _connection, "MGET", "a", "b", "missing");

        response.Type.Should().Be(RespType.Array);
        response.Array![0].AsUtf8String().Should().Be("1");
        response.Array[1].AsUtf8String().Should().Be("2");
        response.Array[2].IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public async Task Del_RemovesKeys()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "del-me", "x");

        var deleted = await CommandTestHelper.ExecuteAsync(_provider, _connection, "DEL", "del-me", "absent");

        deleted.Integer.Should().Be(1);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "del-me")).IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public async Task Exists_CountsPresentKeys()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "e1", "1");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "e2", "2");

        var exists = await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXISTS", "e1", "e2", "missing");

        exists.Integer.Should().Be(2);
    }

    [Fact]
    public async Task IncrAndDecr_UpdateIntegerValue()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "n", "10");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "INCR", "n")).Integer.Should().Be(11);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "DECR", "n")).Integer.Should().Be(10);
    }

    [Fact]
    public async Task Append_ExtendsExistingString()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "append-key", "foo");

        var length = await CommandTestHelper.ExecuteAsync(_provider, _connection, "APPEND", "append-key", "bar");
        var value = await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "append-key");

        length.Integer.Should().Be(6);
        CommandTestHelper.GetBulkText(value).Should().Be("foobar");
    }
}
