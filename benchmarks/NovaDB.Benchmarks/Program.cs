using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Models;
using NovaDB.Protocol;
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
