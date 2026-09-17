using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class HandshakeCommandTests : IAsyncLifetime
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
    public async Task Hello2_ReturnsServerMap()
    {
        var reply = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HELLO", "2");

        reply.Type.Should().Be(RespType.Array);
        reply.Array!.Should().Contain(v => v.Type == RespType.BulkString && v.AsUtf8String() == "novadb");
        reply.Array.Should().Contain(v => v.Type == RespType.Integer && v.Integer == 2);
    }

    [Fact]
    public async Task Hello3_IsRejectedWithNoproto()
    {
        var reply = await CommandTestHelper.ExecuteAsync(_provider, _connection, "HELLO", "3");

        reply.Type.Should().Be(RespType.Error);
        reply.Simple.Should().Contain("NOPROTO");
    }

    [Fact]
    public async Task Select0_Succeeds_AndOtherDbFails()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SELECT", "0"))
            .Should().Be(RespValue.Ok);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SELECT", "1"))
            .Type.Should().Be(RespType.Error);
    }

    [Fact]
    public async Task ConfigGet_ReturnsKnownKeys()
    {
        var reply = await CommandTestHelper.ExecuteAsync(_provider, _connection, "CONFIG", "GET", "*");

        reply.Type.Should().Be(RespType.Array);
        reply.Array!.Select(v => v.Type == RespType.BulkString ? v.AsUtf8String() : null)
            .Should().Contain("appendonly");
    }

    [Fact]
    public async Task ClientSetNameAndSetInfo_RoundTrip()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "CLIENT", "SETNAME", "worker-1"))
            .Should().Be(RespValue.Ok);
        CommandTestHelper.GetBulkText(
                await CommandTestHelper.ExecuteAsync(_provider, _connection, "CLIENT", "GETNAME"))
            .Should().Be("worker-1");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "CLIENT", "SETINFO", "LIB-NAME", "StackExchange.Redis"))
            .Should().Be(RespValue.Ok);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "CLIENT", "SETINFO", "LIB-VER", "2.0"))
            .Should().Be(RespValue.Ok);
    }
}
