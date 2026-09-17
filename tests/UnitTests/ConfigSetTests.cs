using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Eviction;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class ConfigSetTests : IAsyncLifetime
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
    public async Task ConfigSet_MaxmemoryPolicy_UpdatesLivePolicy()
    {
        (await CommandTestHelper.ExecuteAsync(
                _provider, _connection, "CONFIG", "SET", "maxmemory-policy", "allkeys-lru"))
            .Should().Be(RespValue.Ok);

        _provider.GetRequiredService<ConfigurableEvictionPolicy>().PolicyName
            .Should().Be("allkeys-lru");

        var get = await CommandTestHelper.ExecuteAsync(
            _provider, _connection, "CONFIG", "GET", "maxmemory-policy");
        get.Array![1].AsUtf8String().Should().Be("allkeys-lru");
    }

    [Fact]
    public async Task ConfigSet_Maxmemory_UpdatesEngineLimit()
    {
        (await CommandTestHelper.ExecuteAsync(
                _provider, _connection, "CONFIG", "SET", "maxmemory", "1048576"))
            .Should().Be(RespValue.Ok);

        _provider.GetRequiredService<MemoryStorageEngine>().MemoryLimitBytes.Should().Be(1_048_576);
    }

    [Fact]
    public async Task ConfigSet_UnknownPolicy_Errors()
    {
        var response = await CommandTestHelper.ExecuteAsync(
            _provider, _connection, "CONFIG", "SET", "maxmemory-policy", "not-a-policy");
        response.Type.Should().Be(RespType.Error);
    }
}
