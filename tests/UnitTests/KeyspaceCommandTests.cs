using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NovaDB.Protocol;
using UnitTests.Helpers;

namespace UnitTests;

public sealed class KeyspaceCommandTests : IAsyncLifetime
{
    private ServiceProvider _provider = null!;
    private FakeClientConnection _connection = null!;

    public Task InitializeAsync()
    {
        _provider = TestHostFactory.Create();
        _connection = new FakeClientConnection();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await TestHostFactory.DisposeAsync(_provider);

    [Fact]
    public async Task Dbsize_Keys_Scan_AndFlushdb_WorkTogether()
    {
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "user:1", "a");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "user:2", "b");
        await CommandTestHelper.ExecuteAsync(_provider, _connection, "SET", "other", "c");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "DBSIZE")).Integer.Should().Be(3);

        var keys = await CommandTestHelper.ExecuteAsync(_provider, _connection, "KEYS", "user:*");
        keys.Array!.Should().HaveCount(2);

        // Full scan may take multiple opaque cursors; iterate until cursor wraps to 0.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cursor = "0";
        do
        {
            var scan = await CommandTestHelper.ExecuteAsync(
                _provider, _connection, "SCAN", cursor, "MATCH", "*", "COUNT", "10");
            scan.Array!.Should().HaveCount(2);
            cursor = scan.Array[0].AsUtf8String();
            foreach (var key in scan.Array[1].Array!)
            {
                seen.Add(key.AsUtf8String());
            }
        }
        while (cursor != "0");

        seen.Should().BeEquivalentTo("user:1", "user:2", "other");

        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "FLUSHDB")).Should().Be(RespValue.Ok);
        (await CommandTestHelper.ExecuteAsync(_provider, _connection, "DBSIZE")).Integer.Should().Be(0);
    }
}
