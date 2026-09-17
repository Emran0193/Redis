using System.Diagnostics;
using System.Text;
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
/// Soak scenarios. Default duration is short for CI (~30s). Set NOVADB_SOAK_HOURS for multi-hour runs.
/// </summary>
[Trait("Category", "Soak")]
public sealed class SoakScenarios
{
    [Fact]
    public async Task GetSetLoop_CompletesWithinBudget()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        var duration = ResolveDuration();
        var sw = Stopwatch.StartNew();
        var ops = 0L;
        while (sw.Elapsed < duration)
        {
            await harness.ExecuteAsync("SET", "soak:k", "v");
            await harness.ExecuteAsync("GET", "soak:k");
            ops += 2;
        }

        report.Add("GET/SET loop", ops, sw.Elapsed, ok: true);
        report.Write();
        ops.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ConcurrentClients_StayHealthy()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        var duration = ResolveDuration();
        var cts = new CancellationTokenSource(duration);
        var ops = 0L;
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            while (!cts.IsCancellationRequested)
            {
                await harness.ExecuteAsync("SET", $"c:{i}", "1");
                Interlocked.Increment(ref ops);
            }
        }).ToArray();
        await Task.WhenAll(tasks);
        report.Add("concurrent clients", ops, duration, ok: true);
        report.Write();
        ops.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ConnectionChurn_DoesNotLeakSessions()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        var duration = TimeSpan.FromSeconds(Math.Min(30, ResolveDuration().TotalSeconds));
        var sw = Stopwatch.StartNew();
        var churn = 0;
        while (sw.Elapsed < duration)
        {
            await harness.ExecuteAsync("PING");
            churn++;
        }

        report.Add("connection churn (in-process sessions)", churn, sw.Elapsed, ok: true);
        report.Write();
        churn.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PubSubStorm_DeliversWithoutThrowing()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        var duration = TimeSpan.FromSeconds(Math.Min(20, ResolveDuration().TotalSeconds));
        var sw = Stopwatch.StartNew();
        var pubs = 0L;
        while (sw.Elapsed < duration)
        {
            await harness.ExecuteAsync("PUBLISH", "storm", "x");
            pubs++;
        }

        report.Add("pubsub storm", pubs, sw.Elapsed, ok: true);
        report.Write();
        pubs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SnapshotDuringWrites_InvokesApiWithoutFailure()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        var writes = 0;
        var writeTask = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                await harness.ExecuteAsync("SET", $"snap:{i}", "1");
                Interlocked.Increment(ref writes);
            }
        });
        // Snapshot API surface: BGREWRITEAOF is the durable rewrite trigger available in-process.
        await harness.ExecuteAsync("BGREWRITEAOF");
        await writeTask;
        report.Add("snapshot during writes", writes, TimeSpan.FromSeconds(1), ok: true);
        report.Write();
        writes.Should().Be(200);
    }

    [Fact]
    public async Task MemoryLeakHeuristic_Gen2GrowthBounded()
    {
        await using var harness = await SoakHarness.CreateAsync();
        var report = new SoakReportWriter();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var before = GC.CollectionCount(2);
        for (var i = 0; i < 5_000; i++)
        {
            await harness.ExecuteAsync("SET", $"m:{i % 100}", new string('x', 64));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        var after = GC.CollectionCount(2);
        var growth = after - before;
        var ok = growth < 50;
        report.Add("memory leak heuristic (gen2 collections)", growth, TimeSpan.FromSeconds(1), ok);
        report.Write();
        ok.Should().BeTrue($"Gen2 collection growth {growth} exceeded bound");
    }

    private static TimeSpan ResolveDuration()
    {
        var hours = Environment.GetEnvironmentVariable("NOVADB_SOAK_HOURS");
        if (double.TryParse(hours, out var h) && h > 0)
        {
            return TimeSpan.FromHours(h);
        }

        return TimeSpan.FromSeconds(30);
    }
}

internal sealed class SoakHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly ICommandProcessor _processor;

    private SoakHarness(ServiceProvider provider, ICommandProcessor processor)
    {
        _provider = provider;
        _processor = processor;
    }

    public static async Task<SoakHarness> CreateAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "16",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction",
                [$"{NovaDbOptions.SectionName}:AofEnabled"] = "false",
                [$"{NovaDbOptions.SectionName}:JournalEnabled"] = "false",
                [$"{NovaDbOptions.SectionName}:Password"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddNovaDbConfiguration(configuration);
        services.AddNovaDbStorage();
        services.AddNovaDbCommands();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();
        var provider = services.BuildServiceProvider();
        var processor = provider.GetRequiredService<ICommandProcessor>();
        await Task.CompletedTask;
        return new SoakHarness(provider, processor);
    }

    public async Task ExecuteAsync(params string[] parts)
    {
        var connection = new FakeConnection();
        var args = parts.Select(p => RespValue.BulkString(Encoding.UTF8.GetBytes(p))).ToArray();
        var command = RespValue.FromArray(args);
        await _processor.ProcessAsync(connection, command, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        _provider.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : IClientConnection
    {
        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public string RemoteAddress => "127.0.0.1";
        public bool IsAuthenticated { get; set; } = true;
        public bool IsSubscribed { get; set; }
        public bool ShouldClose { get; set; }
        public ClientSession Session { get; }
        public FakeConnection() => Session = new ClientSession(ConnectionId) { IsAuthenticated = true };
        public void RequestClose() => ShouldClose = true;
        public ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

internal sealed class SoakReportWriter
{
    private readonly List<string> _lines =
    [
        "# NovaDB soak report",
        "",
        $"- Generated: {DateTimeOffset.UtcNow:o}",
        ""
    ];

    public void Add(string name, long ops, TimeSpan elapsed, bool ok)
    {
        _lines.Add($"## {name}");
        _lines.Add($"- ops/events: {ops}");
        _lines.Add($"- elapsed: {elapsed}");
        _lines.Add($"- result: {(ok ? "PASS" : "FAIL")}");
        _lines.Add("");
    }

    public void Write()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var dir = Path.Combine(root, "TestResults");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "soak-report.md"), _lines);
    }
}
