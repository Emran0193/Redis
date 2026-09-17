using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Monitoring;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class InfoCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        // Ensure metrics singleton is the real one when monitoring is registered — unit host uses Null.
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await TestHostFactory.DisposeAsync(_provider);

    [Fact]
    public async Task Info_ContainsStatsAndReplicationHonesty()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "k", "v");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "k");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "missing");

        var info = await CommandTestHelper.ExecuteAsync(_provider, _connection, "INFO");
        var text = info.AsUtf8String();

        text.Should().Contain("keyspace_hits:");
        text.Should().Contain("keyspace_misses:");
        text.Should().Contain("role:master");
        text.Should().Contain("connected_slaves:0");
        text.Should().Contain("aof_enabled:");
    }

    [Fact]
    public async Task MemoryStats_ReturnsBulkText()
    {
        var response = await CommandTestHelper.ExecuteAsync(_provider, _connection, "MEMORY", "STATS");
        response.AsUtf8String().Should().Contain("used_memory:");
    }
}
