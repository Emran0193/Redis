using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace IntegrationTests;

/// <summary>Multi-client pipelined soak — proves cache clients can batch without stalling.</summary>
public sealed class PipelineSoakIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task ConcurrentClients_PipelinedSetGetIncr()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);

        const int clients = 8;
        const int batches = 25;
        const int pipelineDepth = 8;
        var latenciesMs = new long[clients];

        var tasks = new Task[clients];
        for (var c = 0; c < clients; c++)
        {
            var id = c;
            tasks[id] = Task.Run(async () =>
            {
                using var client = await server.ConnectAsync(cts.Token);
                var stream = client.GetStream();
                var sw = Stopwatch.StartNew();

                for (var b = 0; b < batches; b++)
                {
                    var payload = new StringBuilder();
                    for (var i = 0; i < pipelineDepth; i++)
                    {
                        var key = $"p{id}:{b}:{i}";
                        var counter = $"c{id}";
                        payload.Append($"*3\r\n$3\r\nSET\r\n${key.Length}\r\n{key}\r\n$1\r\nv\r\n");
                        payload.Append($"*2\r\n$3\r\nGET\r\n${key.Length}\r\n{key}\r\n");
                        payload.Append($"*2\r\n$4\r\nINCR\r\n${counter.Length}\r\n{counter}\r\n");
                    }

                    await stream.WriteAsync(Encoding.UTF8.GetBytes(payload.ToString()), cts.Token);

                    for (var i = 0; i < pipelineDepth; i++)
                    {
                        (await ReadExactAsync(stream, "+OK\r\n".Length, cts.Token)).Should().Be("+OK\r\n");
                        (await ReadExactAsync(stream, "$1\r\nv\r\n".Length, cts.Token)).Should().Be("$1\r\nv\r\n");
                        var incr = await ReadUntilCrLfAsync(stream, cts.Token);
                        incr.Should().StartWith(":");
                    }
                }

                sw.Stop();
                latenciesMs[id] = sw.ElapsedMilliseconds;
            }, cts.Token);
        }

        await Task.WhenAll(tasks);
        latenciesMs.Max().Should().BeLessThan(30_000);
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

    private static async Task<string> ReadUntilCrLfAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(16);
        while (true)
        {
            var b = new byte[1];
            var read = await stream.ReadAsync(b, ct);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            buffer.Add(b[0]);
            if (buffer.Count >= 2
                && buffer[^2] == (byte)'\r'
                && buffer[^1] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }
    }
}
