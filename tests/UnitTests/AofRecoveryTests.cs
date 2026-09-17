using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Persistence.Aof;
using NovaDB.Persistence.Snapshot;
using NovaDB.Protocol;
using NovaDB.Storage;
using UnitTests.Helpers;

namespace UnitTests;

/// <summary>
/// End-to-end recovery tests: write commands, shut down, restart against the same data directory,
/// and require the dataset to come back exactly as it was.
/// </summary>
public sealed class AofRecoveryTests : IAsyncLifetime
{
    private string _dataDirectory = null!;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "novadb-aof", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Restart_RestoresEveryMutatingCommand()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            await Execute(provider, connection, "SET", "text", "hello");
            await Execute(provider, connection, "APPEND", "text", " world");
            await Execute(provider, connection, "INCR", "counter");
            await Execute(provider, connection, "INCR", "counter");
            await Execute(provider, connection, "DECR", "counter");
            await Execute(provider, connection, "MSET", "m1", "a", "m2", "b");
            await Execute(provider, connection, "RPUSH", "list", "a", "b", "c");
            await Execute(provider, connection, "LPOP", "list");
            await Execute(provider, connection, "SADD", "set", "x", "y");
            await Execute(provider, connection, "SREM", "set", "x");
            await Execute(provider, connection, "HSET", "hash", "f1", "v1");
            await Execute(provider, connection, "HSET", "hash", "f2", "v2");
            await Execute(provider, connection, "HDEL", "hash", "f2");
            await Execute(provider, connection, "ZADD", "zset", "1", "one");
            await Execute(provider, connection, "SET", "gone", "x");
            await Execute(provider, connection, "DEL", "gone");
        });

        await WithServerAsync(async (provider, connection) =>
        {
            // APPEND, INCR, DECR, LPOP, SREM and HDEL were all missing from the old hand-written
            // replayer, so each of these assertions used to fail after a restart.
            CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", "text"))
                .Should().Be("hello world");
            CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", "counter"))
                .Should().Be("1");
            CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", "m2"))
                .Should().Be("b");

            var list = await Execute(provider, connection, "LRANGE", "list", "0", "-1");
            list.Array!.Select(item => item.AsUtf8String()).Should().Equal("b", "c");

            (await Execute(provider, connection, "SMEMBERS", "set")).Array!.Should().HaveCount(1);
            (await Execute(provider, connection, "HGETALL", "hash")).Array!.Should().HaveCount(2);
            (await Execute(provider, connection, "ZSCORE", "zset", "one")).IsNullBulk.Should().BeFalse();
            (await Execute(provider, connection, "EXISTS", "gone")).Integer.Should().Be(0);
        });
    }

    [Fact]
    public async Task Restart_RestoresMultiExecMutations()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            await Execute(provider, connection, "MULTI");
            await Execute(provider, connection, "SET", "tx-a", "1");
            await Execute(provider, connection, "SET", "tx-b", "2");
            await Execute(provider, connection, "INCR", "tx-a");
            var exec = await Execute(provider, connection, "EXEC");
            exec.Type.Should().Be(RespType.Array);
            exec.Array!.Should().HaveCount(3);
        });

        await WithServerAsync(async (provider, connection) =>
        {
            CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", "tx-a"))
                .Should().Be("2");
            CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", "tx-b"))
                .Should().Be("2");
        });
    }

    [Fact]
    public async Task Restart_WithSnapshotPresent_DoesNotReapplyAppendCommands()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            await Execute(provider, connection, "RPUSH", "list", "a", "b", "c");
            await Execute(provider, connection, "SADD", "set", "x", "y");
            await Execute(provider, connection, "HSET", "hash", "f", "v");

            // A snapshot alongside the AOF used to mean the dataset was loaded twice: once from the
            // snapshot and again by replaying the log on top of it.
            await provider.GetRequiredService<ISnapshotStore>()
                .SaveAsync(provider.GetRequiredService<IStorageEngine>(), CancellationToken.None);
        });

        await WithServerAsync(async (provider, connection) =>
        {
            var list = await Execute(provider, connection, "LRANGE", "list", "0", "-1");
            list.Array!.Select(item => item.AsUtf8String()).Should().Equal("a", "b", "c");
            (await Execute(provider, connection, "SMEMBERS", "set")).Array!.Should().HaveCount(2);
            (await Execute(provider, connection, "HGETALL", "hash")).Array!.Should().HaveCount(2);
        });
    }

    [Fact]
    public async Task Restart_KeepsExpirationAbsolute()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            await Execute(provider, connection, "SET", "with-ttl", "v", "EX", "100");
            await Execute(provider, connection, "SET", "expiring", "v", "PX", "40");
            await Execute(provider, connection, "SET", "via-expire", "v");
            await Execute(provider, connection, "EXPIRE", "via-expire", "100");

            // Let the short TTL lapse while the server is down.
            await Task.Delay(120);
        });

        await WithServerAsync(async (provider, connection) =>
        {
            var ttl = await Execute(provider, connection, "TTL", "with-ttl");
            ttl.Integer.Should().BeInRange(1, 100, "a replayed TTL must not restart from the replay clock");

            (await Execute(provider, connection, "TTL", "via-expire")).Integer.Should().BeInRange(1, 100);

            (await Execute(provider, connection, "GET", "expiring")).IsNullBulk
                .Should().BeTrue("a key that expired while the server was down must not come back");
        });
    }

    [Fact]
    public async Task Restart_WithCorruptAof_FailsClosed()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            await Execute(provider, connection, "SET", "k", "v");
        });

        var aofPath = Path.Combine(_dataDirectory, "appendonly.aof");
        await File.AppendAllTextAsync(aofPath, "*2\r\n$6\r\nNOSUCH\r\n$1\r\nk\r\n");

        // Silently ignoring a record we cannot apply would start the server with a dataset that
        // does not match the log, so recovery must refuse to start instead.
        var start = () => TestHostFactory.CreatePersistent(_dataDirectory);

        start.Should().Throw<Exception>();
    }

    [Fact]
    public async Task Snapshot_ThenRewrite_KeepsAofBoundedAndRecoverable()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            for (var i = 0; i < 50; i++)
            {
                await Execute(provider, connection, "SET", $"k{i}", "v");
            }

            var before = new FileInfo(Path.Combine(_dataDirectory, "appendonly.aof")).Length;
            before.Should().BeGreaterThan(0);

            await provider.GetRequiredService<ISnapshotStore>()
                .SaveAsync(provider.GetRequiredService<IStorageEngine>(), CancellationToken.None);
            await provider.GetRequiredService<IAofLog>()
                .RewriteFromStorageAsync(provider.GetRequiredService<IStorageEngine>(), CancellationToken.None);

            var after = new FileInfo(Path.Combine(_dataDirectory, "appendonly.aof")).Length;
            after.Should().BeLessThan(before * 2, "rewrite should not grow unboundedly vs the live dataset");
        });

        await WithServerAsync(async (provider, connection) =>
        {
            for (var i = 0; i < 50; i++)
            {
                CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", $"k{i}"))
                    .Should().Be("v");
            }
        });
    }

    [Fact]
    public async Task BgRewriteAof_StartsRewrite_AndRejectsConcurrentCall()
    {
        await WithServerAsync(async (provider, connection) =>
        {
            for (var i = 0; i < 20; i++)
            {
                await Execute(provider, connection, "SET", $"k{i}", "v");
            }

            var storage = provider.GetRequiredService<IStorageEngine>();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holding = storage.RunExclusiveAsync(
                async _ =>
                {
                    await release.Task.ConfigureAwait(false);
                    return true;
                },
                CancellationToken.None);

            await Task.Delay(50);

            var started = await CommandTestHelper.ExecuteAsync(provider, connection, "BGREWRITEAOF");
            started.Type.Should().Be(RespType.SimpleString);
            started.Simple.Should().Contain("started");

            var busy = await CommandTestHelper.ExecuteAsync(provider, connection, "BGREWRITEAOF");
            busy.Type.Should().Be(RespType.Error);
            busy.Simple.Should().Contain("already in progress");

            release.SetResult();
            await holding;

            // Let the scheduled rewrite finish so Dispose does not race the file.
            await Task.Delay(500);
        });

        await WithServerAsync(async (provider, connection) =>
        {
            for (var i = 0; i < 20; i++)
            {
                CommandTestHelper.GetBulkText(await Execute(provider, connection, "GET", $"k{i}"))
                    .Should().Be("v");
            }
        });
    }

    private async Task WithServerAsync(Func<ServiceProvider, FakeClientConnection, Task> body)
    {
        var provider = TestHostFactory.CreatePersistent(_dataDirectory);
        try
        {
            await body(provider, new FakeClientConnection());
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }
    }

    /// <summary>
    /// Executes a command and fails the test if the server rejected it, so a typo or an arity
    /// mistake in the setup cannot be mistaken for a recovery bug.
    /// </summary>
    private static async Task<RespValue> Execute(IServiceProvider provider, FakeClientConnection connection, params string[] parts)
    {
        var response = await CommandTestHelper.ExecuteAsync(provider, connection, parts);
        response.Type.Should().NotBe(RespType.Error, "'{0}' failed: {1}", string.Join(' ', parts), response.Simple);
        return response;
    }
}
