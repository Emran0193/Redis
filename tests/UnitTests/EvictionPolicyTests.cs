using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Storage;
using NovaDB.Storage.Values;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class EvictionPolicyTests
{
    [Fact]
    public async Task NoEviction_ThrowsOutOfMemoryWhenLimitExceeded()
    {
        var provider = TestHostFactory.CreateStorage(options =>
        {
            options.ShardCount = 1;
            options.EvictionPolicy = "noeviction";
            options.MemoryLimitBytes = 128;
        });

        try
        {
            var storage = provider.GetRequiredService<IStorageEngine>();
            var largePayload = new byte[256];
            Array.Fill(largePayload, (byte)'x');
            var key = RedisKey.FromString("oom-key");
            var entry = new DatabaseEntry(RedisValueType.String, new StringValue(largePayload));

            var act = () => storage.SetAsync(key, entry, CancellationToken.None).AsTask();

            await act.Should().ThrowAsync<OutOfMemoryNovaDbException>();
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    /// <summary>
    /// Eviction used to run while the writing shard's lock was held, and it locked every other
    /// shard in turn, so two concurrent writes on different shards could deadlock.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_OverMemoryLimit_EvictWithoutDeadlocking()
    {
        const long limit = 64 * 1024;

        var provider = TestHostFactory.CreateStorage(options =>
        {
            options.ShardCount = 8;
            options.EvictionPolicy = "lru";
            options.MemoryLimitBytes = limit;
        });

        try
        {
            var storage = provider.GetRequiredService<IStorageEngine>();
            var payload = new byte[512];
            Array.Fill(payload, (byte)'x');

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writers = Enumerable.Range(0, 16).Select(writer => Task.Run(async () =>
            {
                await gate.Task;
                for (var i = 0; i < 100; i++)
                {
                    var entry = new DatabaseEntry(RedisValueType.String, new StringValue(payload));
                    await storage.SetAsync(RedisKey.FromString($"evict:{writer}:{i}"), entry, CancellationToken.None);
                }
            })).ToArray();

            gate.SetResult();
            await Task.WhenAll(writers).WaitAsync(TimeSpan.FromSeconds(30));

            storage.EstimatedMemoryBytes.Should().BeLessThanOrEqualTo(
                limit * 2,
                "eviction should keep usage near the configured limit");
            storage.KeyCount.Should().BeLessThan(1600);
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    [Fact]
    public async Task AllKeysLru_UnderPressure_KeepsLatencyBounded()
    {
        const long limit = 32 * 1024;
        var provider = TestHostFactory.CreateStorage(options =>
        {
            options.ShardCount = 4;
            options.EvictionPolicy = "allkeys-lru";
            options.MemoryLimitBytes = limit;
        });

        try
        {
            var storage = provider.GetRequiredService<IStorageEngine>();
            var payload = new byte[256];
            Array.Fill(payload, (byte)'p');
            var samples = new List<long>(800);

            for (var i = 0; i < 800; i++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await storage.SetAsync(
                    RedisKey.FromString($"pressure:{i}"),
                    new DatabaseEntry(RedisValueType.String, new StringValue(payload)),
                    CancellationToken.None);
                sw.Stop();
                samples.Add(sw.Elapsed.Ticks);
            }

            storage.EstimatedMemoryBytes.Should().BeLessThanOrEqualTo(limit * 3);
            storage.KeyCount.Should().BeLessThan(800);

            samples.Sort();
            var p99Index = (int)(samples.Count * 0.99) - 1;
            if (p99Index < 0)
            {
                p99Index = samples.Count - 1;
            }

            var p99Ms = TimeSpan.FromTicks(samples[p99Index]).TotalMilliseconds;
            p99Ms.Should().BeLessThan(
                50,
                "maxmemory eviction under write pressure should stay interactive for showcase demos");
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    [Fact]
    public async Task NoEviction_AllowsWriteWithinMemoryLimit()
    {
        var provider = TestHostFactory.CreateStorage(options =>
        {
            options.ShardCount = 1;
            options.EvictionPolicy = "noeviction";
            options.MemoryLimitBytes = 4096;
        });

        try
        {
            var storage = provider.GetRequiredService<IStorageEngine>();
            var key = RedisKey.FromString("small");
            var entry = new DatabaseEntry(RedisValueType.String, new StringValue("ok"u8.ToArray()));

            await storage.SetAsync(key, entry, CancellationToken.None);
            var loaded = await storage.GetAsync(key, CancellationToken.None);

            loaded.Should().NotBeNull();
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    [Fact]
    public async Task VolatileLru_PrefersKeysWithTtl()
    {
        var policy = new NovaDB.Storage.Eviction.VolatileOnlyEvictionPolicy(
            new NovaDB.Storage.Eviction.LruEvictionPolicy());

        var candidates = new[]
        {
            new NovaDB.Storage.Eviction.EvictionCandidate(
                RedisKey.FromString("persistent"),
                LastAccessTicks: 1,
                AccessFrequency: 1,
                ExpireAtUnixMs: null,
                EstimatedMemoryBytes: 100),
            new NovaDB.Storage.Eviction.EvictionCandidate(
                RedisKey.FromString("volatile"),
                LastAccessTicks: 100,
                AccessFrequency: 1,
                ExpireAtUnixMs: 1,
                EstimatedMemoryBytes: 100),
        };

        policy.TrySelectKey(candidates, out var selected).Should().BeTrue();
        selected.Should().Be(RedisKey.FromString("volatile"));
    }
}
