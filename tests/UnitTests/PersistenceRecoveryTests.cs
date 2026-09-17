using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Models;
using NovaDB.Persistence.Snapshot;
using NovaDB.Storage;
using NovaDB.Storage.Eviction;
using NovaDB.Storage.Expiration;
using NovaDB.Storage.Values;

namespace UnitTests;

public sealed class PersistenceRecoveryTests
{
    [Fact]
    public async Task Snapshot_SaveAndLoad_RestoresEntries()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "novadb-snap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = Options.Create(new NovaDbOptions
            {
                DataDirectory = dataDir,
                ShardCount = 4,
                SnapshotCompression = false
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(options);
            services.AddSingleton<IEvictionPolicy, NoEvictionPolicy>();
            services.AddSingleton<IExpirationScheduler, NoOpExpirationScheduler>();
            services.AddSingleton<MemoryStorageEngine>();
            services.AddSingleton<IStorageEngine>(sp => sp.GetRequiredService<MemoryStorageEngine>());
            services.AddSingleton<SnapshotWriter>();
            services.AddSingleton<SnapshotReader>();
            services.AddSingleton<ISnapshotStore, FileSnapshotStore>();

            await using var sp = services.BuildServiceProvider();
            var storage = sp.GetRequiredService<IStorageEngine>();
            var snapshots = sp.GetRequiredService<ISnapshotStore>();

            var key = RedisKey.FromString("persist");
            await storage.SetAsync(key, new DatabaseEntry(RedisValueType.String, new StringValue("hello"u8.ToArray())), CancellationToken.None);
            await snapshots.SaveAsync(storage, CancellationToken.None);

            var loaded = await snapshots.LoadLatestAsync(CancellationToken.None);
            loaded.Should().ContainSingle(e => e.Key.Equals(key));
            ((StringValue)loaded[0].Entry.Value).Bytes.Should().Equal("hello"u8.ToArray());
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Snapshot_UnderConcurrentWrites_DoesNotThrowAndRestoresConsistentCount()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "novadb-snap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = Options.Create(new NovaDbOptions
            {
                DataDirectory = dataDir,
                ShardCount = 8,
                SnapshotCompression = false,
                MemoryLimitBytes = 0
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(options);
            services.AddSingleton<IEvictionPolicy, NoEvictionPolicy>();
            services.AddSingleton<IExpirationScheduler, NoOpExpirationScheduler>();
            services.AddSingleton<MemoryStorageEngine>();
            services.AddSingleton<IStorageEngine>(sp => sp.GetRequiredService<MemoryStorageEngine>());
            services.AddSingleton<SnapshotWriter>();
            services.AddSingleton<SnapshotReader>();
            services.AddSingleton<ISnapshotStore, FileSnapshotStore>();

            await using var sp = services.BuildServiceProvider();
            var storage = sp.GetRequiredService<IStorageEngine>();
            var snapshots = sp.GetRequiredService<ISnapshotStore>();

            for (var i = 0; i < 200; i++)
            {
                await storage.SetAsync(
                    RedisKey.FromString($"k{i}"),
                    new DatabaseEntry(RedisValueType.String, new StringValue("v"u8.ToArray())),
                    CancellationToken.None);
            }

            var writers = Task.Run(async () =>
            {
                for (var i = 200; i < 400; i++)
                {
                    await storage.SetAsync(
                        RedisKey.FromString($"k{i}"),
                        new DatabaseEntry(RedisValueType.String, new StringValue("v"u8.ToArray())),
                        CancellationToken.None);
                }
            });

            await snapshots.SaveAsync(storage, CancellationToken.None);
            await writers;

            var loaded = await snapshots.LoadLatestAsync(CancellationToken.None);
            // Point-in-time: either the pre-writer set or a later consistent set, never a torn mid-mutation.
            loaded.Count.Should().BeInRange(200, 400);
            loaded.Select(e => e.Key.ToString()).Should().OnlyHaveUniqueItems();
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Snapshot_CorruptCurrent_FallsBackToPreviousGeneration()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "novadb-snap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        try
        {
            var options = Options.Create(new NovaDbOptions
            {
                DataDirectory = dataDir,
                ShardCount = 4,
                SnapshotCompression = false,
                SnapshotGenerationCount = 3
            });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(options);
            services.AddSingleton<IEvictionPolicy, NoEvictionPolicy>();
            services.AddSingleton<IExpirationScheduler, NoOpExpirationScheduler>();
            services.AddSingleton<MemoryStorageEngine>();
            services.AddSingleton<IStorageEngine>(sp => sp.GetRequiredService<MemoryStorageEngine>());
            services.AddSingleton<SnapshotWriter>();
            services.AddSingleton<SnapshotReader>();
            services.AddSingleton<ISnapshotStore, FileSnapshotStore>();

            await using var sp = services.BuildServiceProvider();
            var storage = sp.GetRequiredService<IStorageEngine>();
            var snapshots = sp.GetRequiredService<ISnapshotStore>();

            await storage.SetAsync(
                RedisKey.FromString("old"),
                new DatabaseEntry(RedisValueType.String, new StringValue("gen1"u8.ToArray())),
                CancellationToken.None);
            await snapshots.SaveAsync(storage, CancellationToken.None);

            await storage.SetAsync(
                RedisKey.FromString("new"),
                new DatabaseEntry(RedisValueType.String, new StringValue("gen0"u8.ToArray())),
                CancellationToken.None);
            await snapshots.SaveAsync(storage, CancellationToken.None);

            File.Exists(Path.Combine(dataDir, "snapshot.novdb")).Should().BeTrue();
            File.Exists(Path.Combine(dataDir, "snapshot.novdb.1")).Should().BeTrue();

            var currentPath = Path.Combine(dataDir, "snapshot.novdb");
            var bytes = await File.ReadAllBytesAsync(currentPath);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(currentPath, bytes);

            var loaded = await snapshots.LoadLatestAsync(CancellationToken.None);
            loaded.Should().ContainSingle(e => e.Key.ToString() == "old");
            loaded.Should().NotContain(e => e.Key.ToString() == "new");
            ((StringValue)loaded[0].Entry.Value).Bytes.Should().Equal("gen1"u8.ToArray());
        }
        finally
        {
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    private sealed class NoOpExpirationScheduler : IExpirationScheduler
    {
        public void Schedule(RedisKey key, long expireAtUnixMs)
        {
        }

        public void Cancel(RedisKey key)
        {
        }
    }
}
