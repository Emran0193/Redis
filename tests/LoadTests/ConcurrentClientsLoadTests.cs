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

namespace LoadTests;

/// <summary>
/// Concurrent command-path load coverage without starting hosted services.
/// </summary>
[Trait("Category", "Load")]
public sealed class ConcurrentClientsLoadTests
{
    [Fact]
    public async Task ConcurrentInProcessClients_PerformSetGetOperations()
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

        await using var provider = services.BuildServiceProvider();
        var processor = provider.GetRequiredService<ICommandProcessor>();

        const int clientCount = 20;
        const int operationsPerClient = 50;

        var tasks = new Task[clientCount];
        for (var clientId = 0; clientId < clientCount; clientId++)
        {
            var id = clientId;
            tasks[id] = Task.Run(async () =>
            {
                var connection = new LoadTestConnection($"load-{id}");
                for (var i = 0; i < operationsPerClient; i++)
                {
                    var key = $"load:{id}:{i}";
                    var set = await processor.ProcessAsync(connection, Cmd("SET", key, $"v{i}"), CancellationToken.None);
                    set.Type.Should().Be(RespType.SimpleString);

                    var get = await processor.ProcessAsync(connection, Cmd("GET", key), CancellationToken.None);
                    get.AsUtf8String().Should().Be($"v{i}");
                }
            });
        }

        await Task.WhenAll(tasks);
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
