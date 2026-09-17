using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace IntegrationTests;

/// <summary>TCP soak for cache-oriented SET NX / GETEX / INFO path.</summary>
public sealed class CacheTcpSoakIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task ConcurrentClients_SetNxGet_AndInfo()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);

        const int clients = 6;
        const int ops = 40;
        var tasks = new Task[clients];
        for (var c = 0; c < clients; c++)
        {
            var id = c;
            tasks[id] = Task.Run(async () =>
            {
                using var client = await server.ConnectAsync(cts.Token);
                var stream = client.GetStream();
                for (var i = 0; i < ops; i++)
                {
                    var key = $"c{id}:{i}";
                    var set =
                        $"*6\r\n$3\r\nSET\r\n${key.Length}\r\n{key}\r\n$1\r\nv\r\n$2\r\nNX\r\n$2\r\nEX\r\n$2\r\n60\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(set), cts.Token);
                    (await ReadExactAsync(stream, "+OK\r\n".Length, cts.Token)).Should().Be("+OK\r\n");

                    var get = $"*2\r\n$3\r\nGET\r\n${key.Length}\r\n{key}\r\n";
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(get), cts.Token);
                    (await ReadExactAsync(stream, "$1\r\nv\r\n".Length, cts.Token)).Should().Be("$1\r\nv\r\n");
                }
            }, cts.Token);
        }

        await Task.WhenAll(tasks);

        using var verify = await server.ConnectAsync(cts.Token);
        var vs = verify.GetStream();
        await vs.WriteAsync(Encoding.UTF8.GetBytes("*1\r\n$4\r\nINFO\r\n"), cts.Token);
        var info = await ReadUntilAsync(vs, "role:master", cts.Token);
        info.Should().Contain("keyspace_hits:");
        info.Should().Contain("connected_slaves:0");
    }

    private static async Task<string> ReadExactAsync(NetworkStream stream, int byteCount, CancellationToken ct)
    {
        var buffer = new byte[byteCount];
        var total = 0;
        while (total < byteCount)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, byteCount - total), ct);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string marker, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }

            total += read;
            var text = Encoding.UTF8.GetString(buffer, 0, total);
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return text;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
