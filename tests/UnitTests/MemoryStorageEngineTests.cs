using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Storage;
using NovaDB.Storage.Values;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class MemoryStorageEngineTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private IStorageEngine _storage = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.CreateStorage(options => options.ShardCount = 4);
        _storage = _provider.GetRequiredService<IStorageEngine>();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task SetAndGet_ReturnsStoredStringValue()
    {
        var key = RedisKey.FromString("greeting");
        var entry = new DatabaseEntry(RedisValueType.String, new StringValue("hello"u8.ToArray()));

        await _storage.SetAsync(key, entry, CancellationToken.None);
        var loaded = await _storage.GetAsync(key, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Type.Should().Be(RedisValueType.String);
        ((StringValue)loaded.Value).Bytes.Should().Equal("hello"u8.ToArray());
    }

    [Fact]
    public async Task Delete_RemovesExistingKey()
    {
        var key = RedisKey.FromString("temp");
        await _storage.SetAsync(key, new DatabaseEntry(RedisValueType.String, new StringValue("x"u8.ToArray())), CancellationToken.None)
;

        var deleted = await _storage.DeleteAsync(key, CancellationToken.None);
        var exists = await _storage.ExistsAsync(key, CancellationToken.None);

        deleted.Should().BeTrue();
        exists.Should().Be(0);
    }

    [Fact]
    public async Task Exists_ReturnsOneForPresentKey()
    {
        var key = RedisKey.FromString("present");
        await _storage.SetAsync(key, new DatabaseEntry(RedisValueType.String, new StringValue("1"u8.ToArray())), CancellationToken.None)
;

        var exists = await _storage.ExistsAsync(key, CancellationToken.None);

        exists.Should().Be(1);
    }

    [Fact]
    public async Task Increment_CreatesAndIncrementsIntegerString()
    {
        var key = RedisKey.FromString("counter");

        var first = await _storage.IncrementAsync(key, 5, CancellationToken.None);
        var second = await _storage.IncrementAsync(key, 3, CancellationToken.None);

        first.Should().Be(5);
        second.Should().Be(8);
    }

    [Fact]
    public async Task Increment_OnWrongType_ThrowsWrongTypeException()
    {
        var key = RedisKey.FromString("hash-key");
        var hashEntry = new DatabaseEntry(RedisValueType.Hash, new HashValue());
        await _storage.SetAsync(key, hashEntry, CancellationToken.None);

        var act = () => _storage.IncrementAsync(key, 1, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<WrongTypeException>();
    }

    [Fact]
    public async Task ConcurrentSetAndGet_MaintainsConsistency()
    {
        const int keyCount = 200;
        var keys = Enumerable.Range(0, keyCount)
            .Select(i => RedisKey.FromString($"parallel:{i}"))
            .ToArray();

        Parallel.ForEach(keys, key =>
        {
            var entry = new DatabaseEntry(RedisValueType.String, new StringValue(Encoding.UTF8.GetBytes(key.ToString())));
            _storage.SetAsync(key, entry, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        });

        Parallel.ForEach(keys, key =>
        {
            var loaded = _storage.GetAsync(key, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            loaded.Should().NotBeNull();
            Encoding.UTF8.GetString(((StringValue)loaded!.Value).Bytes).Should().Be(key.ToString());
        });

        _storage.KeyCount.Should().Be(keyCount);
    }

    [Fact]
    public async Task Sharding_DistributesKeysAcrossConfiguredShards()
    {
        const int shardCount = 4;
        var provider = TestHostFactory.CreateStorage(options => options.ShardCount = shardCount);
        var engine = provider.GetRequiredService<MemoryStorageEngine>();

        var shardHits = new int[shardCount];
        for (var i = 0; i < 500; i++)
        {
            var key = RedisKey.FromString($"shard-test:{i}");
            var shardIndex = HashSlot.ComputeShardIndex(key.Bytes, shardCount);
            shardHits[shardIndex]++;

            await engine.SetAsync(
                key,
                new DatabaseEntry(RedisValueType.String, new StringValue("v"u8.ToArray())),
                CancellationToken.None);
        }

        shardHits.Count(h => h > 0).Should().BeGreaterThan(1, "keys should land on multiple shards");
        engine.KeyCount.Should().Be(500);

        await TestHostFactory.DisposeAsync(provider);
    }
}
