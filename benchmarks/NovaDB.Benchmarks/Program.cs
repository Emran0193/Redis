using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Storage.Eviction;
using NovaDB.Storage.Expiration;
using NovaDB.Storage.Values;
using System.Buffers;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>
/// BenchmarkDotNet entry point.
/// </summary>
public partial class Program;

/// <summary>
/// No-op expiration scheduler for benchmarks.
/// </summary>
file sealed class NullExpirationScheduler : IExpirationScheduler
{
    public void Schedule(RedisKey key, long expireAtUnixMs)
    {
    }

    public void Cancel(RedisKey key)
    {
    }
}

/// <summary>
/// Measures in-process SET/GET throughput against <see cref="MemoryStorageEngine"/>.
/// </summary>
[MemoryDiagnoser]
public class SetGetThroughputBenchmarks
{
    private MemoryStorageEngine _storage = null!;
    private RedisKey _key;
    private DatabaseEntry _entry = null!;

    /// <summary>
    /// Prepares storage and a reusable key/value pair.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var options = Options.Create(new NovaDbOptions { ShardCount = 16, MemoryLimitBytes = 0 });
        _storage = new MemoryStorageEngine(options, new NoEvictionPolicy(), new NullExpirationScheduler());
        _key = RedisKey.FromString("bench:key");
        _entry = new DatabaseEntry(RedisValueType.String, new StringValue("value"u8.ToArray()));
    }

    /// <summary>
    /// Benchmarks SET.
    /// </summary>
    [Benchmark]
    public Task SetAsync()
        => _storage.SetAsync(_key, _entry, CancellationToken.None).AsTask();

    /// <summary>
    /// Benchmarks GET.
    /// </summary>
    [Benchmark]
    public Task GetAsync()
        => _storage.GetAsync(_key, CancellationToken.None).AsTask();
}

/// <summary>
/// Measures MGET of multiple keys.
/// </summary>
[MemoryDiagnoser]
public class MgetThroughputBenchmarks
{
    private MemoryStorageEngine _storage = null!;
    private RedisKey[] _keys = null!;

    /// <summary>Seeds keys for MGET.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var options = Options.Create(new NovaDbOptions { ShardCount = 16, MemoryLimitBytes = 0 });
        _storage = new MemoryStorageEngine(options, new NoEvictionPolicy(), new NullExpirationScheduler());
        _keys = Enumerable.Range(0, 16).Select(i => RedisKey.FromString($"m:{i}")).ToArray();
        foreach (var key in _keys)
        {
            _storage.SetAsync(key, new DatabaseEntry(RedisValueType.String, new StringValue("v"u8.ToArray())), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Benchmarks sequential MGET-style reads.</summary>
    [Benchmark]
    public async Task MgetAsync()
    {
        foreach (var key in _keys)
        {
            await _storage.GetAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Measures RESP parser throughput and allocations.
/// </summary>
[MemoryDiagnoser]
public class RespParserBenchmarks
{
    private byte[] _payload = null!;

    /// <summary>
    /// Builds a representative GET command frame.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var command = RespValue.FromArray(
        [
            RespValue.BulkString("GET"),
            RespValue.BulkString("mykey")
        ]);
        _payload = RespWriter.Serialize(command);
    }

    /// <summary>
    /// Parses one RESP array command.
    /// </summary>
    [Benchmark]
    public RespValue? ParseGetCommand()
    {
        var buffer = new ReadOnlySequence<byte>(_payload);
        RespParser.TryParse(ref buffer, out var value, out _);
        return value;
    }
}

/// <summary>
/// Measures pipelined RESP parse of multiple commands in one buffer.
/// </summary>
[MemoryDiagnoser]
public class PipelineParseBenchmarks
{
    private byte[] _payload = null!;

    /// <summary>Builds a 32-command pipeline buffer.</summary>
    [GlobalSetup]
    public void Setup()
    {
        using var ms = new MemoryStream();
        for (var i = 0; i < 32; i++)
        {
            var frame = RespWriter.Serialize(RespValue.FromArray(
            [
                RespValue.BulkString("SET"),
                RespValue.BulkString($"k{i}"),
                RespValue.BulkString("v")
            ]));
            ms.Write(frame);
        }

        _payload = ms.ToArray();
    }

    /// <summary>Parses all frames in the pipeline buffer.</summary>
    [Benchmark]
    public int ParsePipeline()
    {
        var buffer = new ReadOnlySequence<byte>(_payload);
        var count = 0;
        while (RespParser.TryParse(ref buffer, out _, out var consumed))
        {
            buffer = buffer.Slice(consumed);
            count++;
        }

        return count;
    }
}

/// <summary>
/// Measures in-process pub/sub publish fan-out.
/// </summary>
[MemoryDiagnoser]
public class PubSubBenchmarks
{
    private PubSubHub _hub = null!;

    /// <summary>Creates the hub.</summary>
    [GlobalSetup]
    public void Setup() => _hub = new PubSubHub(NullLogger<PubSubHub>.Instance);

    /// <summary>Publishes a message with no subscribers.</summary>
    [Benchmark]
    public Task<long> Publish() => _hub.PublishAsync("bench", "payload"u8.ToArray(), CancellationToken.None).AsTask();
}

/// <summary>
/// Measures snapshot-sized bulk enumeration of keys (proxy for snapshot scan cost).
/// </summary>
[MemoryDiagnoser]
public class SnapshotScanBenchmarks
{
    private MemoryStorageEngine _storage = null!;

    /// <summary>Seeds the dataset.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var options = Options.Create(new NovaDbOptions { ShardCount = 16, MemoryLimitBytes = 0 });
        _storage = new MemoryStorageEngine(options, new NoEvictionPolicy(), new NullExpirationScheduler());
        for (var i = 0; i < 1_000; i++)
        {
            var key = RedisKey.FromString($"s:{i}");
            _storage.SetAsync(key, new DatabaseEntry(RedisValueType.String, new StringValue("v"u8.ToArray())), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Scans all keys once.</summary>
    [Benchmark]
    public async Task ScanAllAsync()
    {
        await foreach (var _ in _storage.ScanAsync(CancellationToken.None))
        {
        }
    }
}
