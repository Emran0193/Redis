using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class TransactionTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await TestHostFactory.DisposeAsync(_provider);
    }

    [Fact]
    public async Task MultiExec_ExecutesQueuedCommands()
    {
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "MULTI"))
            .Should().Be(RespValue.Ok);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "tx-key", "before"))
            .Simple.Should().Be("QUEUED");
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "tx-key"))
            .Simple.Should().Be("QUEUED");

        var exec = await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXEC");

        exec.Type.Should().Be(RespType.Array);
        exec.Array!.Should().HaveCount(2);
        exec.Array[0].Type.Should().Be(RespType.SimpleString);
        exec.Array[0].Simple.Should().Be("OK");
        CommandTestHelper.GetBulkText(exec.Array[1]).Should().Be("before");

        CommandTestHelper.GetBulkText(
            await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "tx-key"))
            .Should().Be("before");
    }

    [Fact]
    public async Task MultiDiscard_ClearsQueuedCommands()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "MULTI");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "discard-key", "lost");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "DISCARD"))
            .Should().Be(RespValue.Ok);

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "discard-key"))
            .IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public async Task WatchConflict_ReturnsNullBulkOnExec()
    {
        var other = new FakeClientConnection();
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "watched", "v1");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "WATCH", "watched"))
            .Should().Be(RespValue.Ok);
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "MULTI");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "watched");

        await CommandTestHelper.ExecuteAsync(_provider, other, "SET", "watched", "v2");

        var exec = await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXEC");

        exec.IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public async Task Watch_DeleteAndRecreate_IsStillAConflict()
    {
        var other = new FakeClientConnection();
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "aba", "v1");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "WATCH", "aba");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "MULTI");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "aba");

        // Delete + recreate used to reset Version to 1 and pass the WATCH check (ABA).
        await CommandTestHelper.ExecuteAsync(_provider, other, "DEL", "aba");
        await CommandTestHelper.ExecuteAsync(_provider, other, "SET", "aba", "v1");

        var exec = await CommandTestHelper.ExecuteAsync(_provider, _connection, "EXEC");
        exec.IsNullBulk.Should().BeTrue();
    }

    [Fact]
    public async Task Exec_IsAtomicWithRespectToOtherWriters()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "counter", "0");

        var writers = Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
        {
            var connection = new FakeClientConnection($"writer-{i}");
            for (var n = 0; n < 50; n++)
            {
                await CommandTestHelper.ExecuteAsync(_provider, connection, "INCR", "counter");
            }
        })).ToArray();

        var transactions = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
        {
            var connection = new FakeClientConnection($"tx-{i}");
            await CommandTestHelper.ExecuteAsync(_provider, connection, "MULTI");
            await CommandTestHelper.ExecuteAsync(_provider, connection, "INCR", "counter");
            await CommandTestHelper.ExecuteAsync(_provider, connection, "INCR", "counter");
            var exec = await CommandTestHelper.ExecuteAsync(_provider, connection, "EXEC");
            exec.Type.Should().Be(RespType.Array);
            exec.Array!.Should().HaveCount(2);
        })).ToArray();

        await Task.WhenAll(writers.Concat(transactions)).WaitAsync(TimeSpan.FromSeconds(30));

        // 32*50 increments + 16*2 transaction increments, with no lost updates.
        CommandTestHelper.GetBulkText(
                await CommandTestHelper.ExecuteAsync(_provider, _connection, "GET", "counter"))
            .Should().Be((32 * 50 + 16 * 2).ToString());
    }
}
