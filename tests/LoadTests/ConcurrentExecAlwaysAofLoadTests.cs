using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaDB.Commands;
using NovaDB.Commands.Persistence;
using NovaDB.Configuration;
using NovaDB.Core.Sessions;
using NovaDB.Networking;
using NovaDB.Persistence;
using NovaDB.Protocol;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Transactions;

namespace LoadTests;

/// <summary>
/// Concurrent MULTI/EXEC under Always AOF (group-commit path) without TCP.
/// </summary>
[Trait("Category", "Load")]
public sealed class ConcurrentExecAlwaysAofLoadTests
{
    [Fact]
    public async Task ConcurrentMultiExec_WithAlwaysAof_StaysConsistent()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "novadb-load-exec", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "16",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction",
                [$"{NovaDbOptions.SectionName}:AofEnabled"] = "true",
                [$"{NovaDbOptions.SectionName}:AofFlushPolicy"] = "always",
                [$"{NovaDbOptions.SectionName}:SnapshotInterval"] = "01:00:00",
                [$"{NovaDbOptions.SectionName}:DataDirectory"] = dataDirectory,
                [$"{NovaDbOptions.SectionName}:Password"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));
        services.AddNovaDbConfiguration(configuration);
        services.AddNovaDbStorage();
        services.AddNovaDbPersistence();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();
        services.AddNovaDbCommands();
        services.AddNovaDbCommandReplay();

        await using var provider = services.BuildServiceProvider();
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        try
        {
            var processor = provider.GetRequiredService<ICommandProcessor>();
            const int clientCount = 12;
            const int execsPerClient = 40;

            var tasks = new Task[clientCount];
            for (var clientId = 0; clientId < clientCount; clientId++)
            {
                var id = clientId;
                tasks[id] = Task.Run(async () =>
                {
                    var connection = new LoadTestConnection($"exec-load-{id}");
                    for (var i = 0; i < execsPerClient; i++)
                    {
                        var key = $"lexec:{id}:{i}";
                        (await processor.ProcessAsync(connection, Cmd("MULTI"), CancellationToken.None))
                            .Should().Be(RespValue.Ok);
                        (await processor.ProcessAsync(connection, Cmd("SET", key, "10"), CancellationToken.None))
                            .Simple.Should().Be("QUEUED");
                        (await processor.ProcessAsync(connection, Cmd("INCR", key), CancellationToken.None))
                            .Simple.Should().Be("QUEUED");
                        var exec = await processor.ProcessAsync(connection, Cmd("EXEC"), CancellationToken.None);
                        exec.Type.Should().Be(RespType.Array);
                        exec.Array!.Should().HaveCount(2);
                    }
                });
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(90));

            var verify = new LoadTestConnection("verify");
            for (var id = 0; id < clientCount; id++)
            {
                for (var i = 0; i < execsPerClient; i++)
                {
                    var get = await processor.ProcessAsync(
                        verify,
                        Cmd("GET", $"lexec:{id}:{i}"),
                        CancellationToken.None);
                    get.AsUtf8String().Should().Be("11");
                }
            }
        }
        finally
        {
            foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
            {
                await hosted.StopAsync(CancellationToken.None);
            }

            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
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
