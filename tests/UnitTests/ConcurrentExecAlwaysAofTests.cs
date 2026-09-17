using FluentAssertions;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

/// <summary>
/// Soak-style coverage: concurrent MULTI/EXEC under Always AOF must stay correct and recover.
/// </summary>
public sealed class ConcurrentExecAlwaysAofTests : IAsyncLifetime
{
    private string _dataDirectory = null!;

    public Task InitializeAsync()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "novadb-exec-soak", Guid.NewGuid().ToString("N"));
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
    public async Task ConcurrentExec_WithAlwaysAof_CompletesAndRecovers()
    {
        const int clientCount = 8;
        const int execsPerClient = 25;

        var provider = TestHostFactory.CreatePersistent(_dataDirectory, aofFlushPolicy: "always");
        try
        {
            var tasks = new Task[clientCount];
            for (var clientId = 0; clientId < clientCount; clientId++)
            {
                var id = clientId;
                tasks[id] = Task.Run(async () =>
                {
                    var connection = new FakeClientConnection($"exec-soak-{id}");
                    for (var i = 0; i < execsPerClient; i++)
                    {
                        var key = $"soak:{id}:{i}";
                        (await CommandTestHelper.ExecuteAsync(provider, connection, "MULTI"))
                            .Should().Be(RespValue.Ok);
                        (await CommandTestHelper.ExecuteAsync(provider, connection, "SET", key, "1"))
                            .Simple.Should().Be("QUEUED");
                        (await CommandTestHelper.ExecuteAsync(provider, connection, "INCR", key))
                            .Simple.Should().Be("QUEUED");
                        (await CommandTestHelper.ExecuteAsync(provider, connection, "INCR", key))
                            .Simple.Should().Be("QUEUED");

                        var exec = await CommandTestHelper.ExecuteAsync(provider, connection, "EXEC");
                        exec.Type.Should().Be(RespType.Array);
                        exec.Array!.Should().HaveCount(3);
                        exec.Array[2].Integer.Should().Be(3);
                    }
                });
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));

            for (var id = 0; id < clientCount; id++)
            {
                for (var i = 0; i < execsPerClient; i++)
                {
                    var get = await CommandTestHelper.ExecuteAsync(
                        provider,
                        new FakeClientConnection(),
                        "GET",
                        $"soak:{id}:{i}");
                    CommandTestHelper.GetBulkText(get).Should().Be("3");
                }
            }
        }
        finally
        {
            await TestHostFactory.DisposeAsync(provider);
        }

        // Restart against the same Always-flushed AOF and require the full EXEC batch set.
        var restarted = TestHostFactory.CreatePersistent(_dataDirectory, aofFlushPolicy: "always");
        try
        {
            var connection = new FakeClientConnection();
            for (var id = 0; id < clientCount; id++)
            {
                for (var i = 0; i < execsPerClient; i++)
                {
                    var get = await CommandTestHelper.ExecuteAsync(
                        restarted,
                        connection,
                        "GET",
                        $"soak:{id}:{i}");
                    CommandTestHelper.GetBulkText(get).Should().Be("3");
                }
            }
        }
        finally
        {
            await TestHostFactory.DisposeAsync(restarted);
        }
    }
}
