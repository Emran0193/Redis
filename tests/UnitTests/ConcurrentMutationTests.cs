using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

/// <summary>
/// Guards the atomicity of read-modify-write commands. Before mutations moved inside the shard
/// lock, every one of these tests lost updates or tore the underlying collection.
/// </summary>
public sealed class ConcurrentMutationTests : IAsyncLifetime
{
    private const int Writers = 64;
    private const int OperationsPerWriter = 25;
    private const int TotalOperations = Writers * OperationsPerWriter;

    private ServiceProvider _provider = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task ConcurrentRpush_KeepsEveryElement()
    {
        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "RPUSH", "list", $"{writer}-{operation}"));

        var range = await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "LRANGE", "list", "0", "-1");

        range.Type.Should().Be(RespType.Array);
        range.Array!.Should().HaveCount(TotalOperations);
        range.Array.Select(item => item.AsUtf8String()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ConcurrentLpushAndLpop_LeavesConsistentList()
    {
        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "LPUSH", "queue", $"{writer}-{operation}"));

        var popped = 0;
        await RunConcurrentlyAsync(async (connection, writer, operation) =>
        {
            var response = await CommandTestHelper.ExecuteAsync(_provider, connection, "LPOP", "queue");
            if (!response.IsNullBulk)
            {
                Interlocked.Increment(ref popped);
            }

            return response;
        });

        popped.Should().Be(TotalOperations);
        (await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "EXISTS", "queue"))
            .Integer.Should().Be(0);
    }

    [Fact]
    public async Task ConcurrentSadd_KeepsEveryMember()
    {
        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "SADD", "set", $"{writer}-{operation}"));

        var members = await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "SMEMBERS", "set");

        members.Array!.Should().HaveCount(TotalOperations);
    }

    [Fact]
    public async Task ConcurrentHset_KeepsEveryField()
    {
        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "HSET", "hash", $"{writer}-{operation}", "v"));

        var all = await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "HGETALL", "hash");

        all.Array!.Should().HaveCount(TotalOperations * 2, "HGETALL returns a field and a value per entry");
    }

    [Fact]
    public async Task ConcurrentAppend_LosesNoWrite()
    {
        await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "SET", "text", string.Empty);

        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "APPEND", "text", "x"));

        var value = await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "GET", "text");

        CommandTestHelper.GetBulkText(value).Should().HaveLength(TotalOperations);
    }

    [Fact]
    public async Task ConcurrentIncr_CountsEveryIncrement()
    {
        await RunConcurrentlyAsync((connection, writer, operation) =>
            CommandTestHelper.ExecuteAsync(_provider, connection, "INCR", "counter"));

        var value = await CommandTestHelper.ExecuteAsync(_provider, new FakeClientConnection(), "GET", "counter");

        CommandTestHelper.GetBulkText(value).Should().Be(TotalOperations.ToString());
    }

    /// <summary>
    /// Runs <see cref="Writers"/> parallel clients, each issuing <see cref="OperationsPerWriter"/>
    /// commands, and fails the test rather than hanging if the work stalls.
    /// </summary>
    private static async Task RunConcurrentlyAsync(Func<FakeClientConnection, int, int, Task<RespValue>> action)
    {
        // An async gate rather than a Barrier: 64 writers blocking a thread-pool thread each would
        // starve the pool and stall the test rather than exercise concurrency.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            var connection = new FakeClientConnection();
            await gate.Task;

            for (var operation = 0; operation < OperationsPerWriter; operation++)
            {
                await action(connection, writer, operation);
            }
        })).ToArray();

        gate.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
