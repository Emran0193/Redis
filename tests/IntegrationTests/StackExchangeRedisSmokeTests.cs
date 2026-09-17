using FluentAssertions;
using StackExchange.Redis;

namespace IntegrationTests;

/// <summary>
/// Proves NovaDB speaks enough RESP for the dominant .NET Redis client.
/// </summary>
public sealed class StackExchangeRedisSmokeTests
{
    [Fact]
    public async Task StackExchangeRedis_SetGet_AndStringCommands()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);

        await using var mux = await ConnectionMultiplexer.ConnectAsync(
            new ConfigurationOptions
            {
                EndPoints = { { "127.0.0.1", server.Port } },
                AbortOnConnectFail = true,
                ConnectTimeout = 5_000,
                SyncTimeout = 5_000
            });

        var db = mux.GetDatabase();

        (await db.StringSetAsync("ser:key", "hello", TimeSpan.FromSeconds(30), When.NotExists))
            .Should().BeTrue();
        (await db.StringGetAsync("ser:key")).ToString().Should().Be("hello");

        (await db.StringSetAsync("ser:key", "nope", when: When.NotExists)).Should().BeFalse();
        (await db.StringIncrementAsync("ser:counter")).Should().Be(1);
        (await db.KeyExistsAsync("ser:key")).Should().BeTrue();

        var info = await db.ExecuteAsync("INFO", "stats");
        info.ToString().Should().Contain("keyspace_hits");
    }
}
