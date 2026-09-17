using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaDB.Commands;
using NovaDB.Configuration;
using NovaDB.Core.Sessions;
using NovaDB.Networking;
using NovaDB.Protocol;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Transactions;
using Xunit.Abstractions;

namespace LoadTests;

/// <summary>
/// Evidence that ordinary SET/GET no longer serialize on a global DB gate.
/// Multi-acceptor work stays deferred until TCP accept — not command serialization — is the limiter.
/// </summary>
[Trait("Category", "Load")]
public sealed class GateFreeThroughputLoadTests
{
    private readonly ITestOutputHelper _output;

    public GateFreeThroughputLoadTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ConcurrentSetGet_BeatsSequentialWallClock()
    {
        await using var provider = CreateProvider();
        var processor = provider.GetRequiredService<ICommandProcessor>();

        const int operations = 4_000;
        const int parallelClients = 8;
        const int opsPerClient = operations / parallelClients;

        // Warmup so JIT does not dominate the timed windows.
        {
            var warm = new LoadTestConnection("warm");
            for (var i = 0; i < 200; i++)
            {
                await processor.ProcessAsync(warm, Cmd("SET", $"warm:{i}", "1"), CancellationToken.None);
            }
        }

        var sequential = Stopwatch.StartNew();
        {
            var connection = new LoadTestConnection("seq");
            for (var i = 0; i < operations; i++)
            {
                var set = await processor.ProcessAsync(
                    connection,
                    Cmd("SET", $"seq:{i}", "v"),
                    CancellationToken.None);
                set.Type.Should().Be(RespType.SimpleString);
            }
        }

        sequential.Stop();

        var parallel = Stopwatch.StartNew();
        var tasks = new Task[parallelClients];
        for (var clientId = 0; clientId < parallelClients; clientId++)
        {
            var id = clientId;
            tasks[id] = Task.Run(async () =>
            {
                var connection = new LoadTestConnection($"par-{id}");
                for (var i = 0; i < opsPerClient; i++)
                {
                    var set = await processor.ProcessAsync(
                        connection,
                        Cmd("SET", $"par:{id}:{i}", "v"),
                        CancellationToken.None);
                    set.Type.Should().Be(RespType.SimpleString);
                }
            });
        }

        await Task.WhenAll(tasks);
        parallel.Stop();

        var sequentialOps = operations / sequential.Elapsed.TotalSeconds;
        var parallelOps = operations / parallel.Elapsed.TotalSeconds;

        _output.WriteLine(
            "gate-free throughput: sequential={0:F0} ops/s ({1}ms), parallel/{2}={3:F0} ops/s ({4}ms), speedup={5:F2}x",
            sequentialOps,
            sequential.ElapsedMilliseconds,
            parallelClients,
            parallelOps,
            parallel.ElapsedMilliseconds,
            sequential.Elapsed.TotalMilliseconds / Math.Max(1, parallel.Elapsed.TotalMilliseconds));

        // Floor: in-process command path must stay healthy on CI.
        parallelOps.Should().BeGreaterThan(1_000);

        // Under a global exclusive gate, parallel wall time ≈ sequential. After Sprint 2 narrowing,
        // shard-level work should finish faster in parallel on distinct keys.
        parallel.Elapsed.Should().BeLessThan(
            sequential.Elapsed,
            "parallel distinct-key SETs should outpace a single-threaded loop if the global gate is gone");
    }

    private static ServiceProvider CreateProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "16",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction",
                [$"{NovaDbOptions.SectionName}:AofEnabled"] = "false",
                [$"{NovaDbOptions.SectionName}:Password"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddNovaDbConfiguration(configuration);
        services.AddNovaDbStorage();
        services.AddNovaDbCommands();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();
        return services.BuildServiceProvider();
    }

    private static RespValue Cmd(params string[] parts)
    {
        var values = new RespValue[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = RespValue.BulkString(parts[i]);
        }

        return RespValue.FromArray(values);
    }

    private sealed class LoadTestConnection : IClientConnection
    {
        public LoadTestConnection(string connectionId)
        {
            ConnectionId = connectionId;
            Session = new ClientSession(connectionId) { IsAuthenticated = true };
        }

        public string ConnectionId { get; }
        public string RemoteAddress => "127.0.0.1";
        public bool IsAuthenticated
        {
            get => Session.IsAuthenticated;
            set => Session.IsAuthenticated = value;
        }
        public bool IsSubscribed { get; set; }
        public bool ShouldClose { get; set; }
        public ClientSession Session { get; }

        public void RequestClose() => ShouldClose = true;

        public ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
