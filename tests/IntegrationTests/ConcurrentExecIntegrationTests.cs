using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace IntegrationTests;

/// <summary>
/// Concurrent TCP clients running MULTI/EXEC — socket-level soak beyond in-process load tests.
/// </summary>
public sealed class ConcurrentExecIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task ConcurrentClients_MultiExec_AllKeysVisible()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);

        const int clientCount = 8;
        const int execsPerClient = 20;

        var tasks = new Task[clientCount];
        for (var clientId = 0; clientId < clientCount; clientId++)
        {
            var id = clientId;
            tasks[id] = Task.Run(async () =>
            {
                using var client = await server.ConnectAsync(cts.Token);
                var stream = client.GetStream();
                for (var i = 0; i < execsPerClient; i++)
                {
                    var key = $"itx:{id}:{i}";
                    var keyBulk = $"${key.Length}\r\n{key}\r\n";
                    var pipeline =
                        "*1\r\n$5\r\nMULTI\r\n" +
                        $"*3\r\n$3\r\nSET\r\n{keyBulk}$1\r\n7\r\n" +
                        $"*2\r\n$4\r\nINCR\r\n{keyBulk}" +
                        "*1\r\n$4\r\nEXEC\r\n";

                    // +OK, +QUEUED, +QUEUED, then EXEC array: *2 / +OK / :8
                    var expected =
                        "+OK\r\n+QUEUED\r\n+QUEUED\r\n*2\r\n+OK\r\n:8\r\n";

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(pipeline), cts.Token);
                    var response = await ReadExactAsync(stream, expected.Length, cts.Token);
                    response.Should().Be(expected);
                }
            }, cts.Token);
        }

        await Task.WhenAll(tasks);

        using var verify = await server.ConnectAsync(cts.Token);
        var verifyStream = verify.GetStream();
        for (var id = 0; id < clientCount; id++)
        {
            for (var i = 0; i < execsPerClient; i++)
            {
                var key = $"itx:{id}:{i}";
                var get = $"*2\r\n$3\r\nGET\r\n${key.Length}\r\n{key}\r\n";
                var expected = "$1\r\n8\r\n";
                await verifyStream.WriteAsync(Encoding.UTF8.GetBytes(get), cts.Token);
                (await ReadExactAsync(verifyStream, expected.Length, cts.Token)).Should().Be(expected);
            }
        }
    }

    private static async Task<string> ReadExactAsync(NetworkStream stream, int byteCount, CancellationToken cancellationToken)
    {
        var buffer = new byte[byteCount];
        var total = 0;
        while (total < byteCount)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, byteCount - total), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException($"Expected {byteCount} bytes, got {total}.");
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer);
    }
}
