using System.Net.Sockets;
using System.Text;
using FluentAssertions;

namespace IntegrationTests;

/// <summary>
/// Covers the case the original test suite never exercised: more than one command on a single
/// socket. The write path used to re-flush until the pipe reader completed, so a connection served
/// exactly one command and then spun on the CPU forever.
/// </summary>
public sealed class ConnectionReuseIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task SequentialCommands_OnOneConnection_AllGetReplies()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);
        using var client = await server.ConnectAsync(cts.Token);
        var stream = client.GetStream();

        (await RoundTripAsync(stream, "*1\r\n$4\r\nPING\r\n", cts.Token)).Should().Be("+PONG\r\n");
        (await RoundTripAsync(stream, "*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n", cts.Token)).Should().Be("+OK\r\n");
        (await RoundTripAsync(stream, "*2\r\n$3\r\nGET\r\n$1\r\nk\r\n", cts.Token)).Should().Be("$1\r\nv\r\n");
        (await RoundTripAsync(stream, "*1\r\n$4\r\nPING\r\n", cts.Token)).Should().Be("+PONG\r\n");
    }

    [Fact]
    public async Task PipelinedCommands_InOneWrite_AllGetRepliesInOrder()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);
        using var client = await server.ConnectAsync(cts.Token);
        var stream = client.GetStream();

        var pipeline =
            "*3\r\n$3\r\nSET\r\n$1\r\na\r\n$1\r\n1\r\n" +
            "*3\r\n$3\r\nSET\r\n$1\r\nb\r\n$1\r\n2\r\n" +
            "*3\r\n$4\r\nMGET\r\n$1\r\na\r\n$1\r\nb\r\n" +
            "*1\r\n$4\r\nPING\r\n";

        var expected = "+OK\r\n+OK\r\n*2\r\n$1\r\n1\r\n$1\r\n2\r\n+PONG\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(pipeline), cts.Token);
        var response = await ReadAtLeastAsync(stream, expected.Length, cts.Token);

        response.Should().Be(expected);
    }

    [Fact]
    public async Task ManyCommands_OnOneConnection_StayResponsive()
    {
        using var cts = new CancellationTokenSource(Timeout);
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);
        using var client = await server.ConnectAsync(cts.Token);
        var stream = client.GetStream();

        for (var i = 0; i < 200; i++)
        {
            var command = $"*3\r\n$3\r\nSET\r\n$4\r\nk{i:D3}\r\n$1\r\nv\r\n";
            (await RoundTripAsync(stream, command, cts.Token)).Should().Be("+OK\r\n");
        }

        (await RoundTripAsync(stream, "*2\r\n$3\r\nGET\r\n$4\r\nk099\r\n", cts.Token)).Should().Be("$1\r\nv\r\n");
    }

    private static async Task<string> RoundTripAsync(NetworkStream stream, string request, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken);

        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static async Task<string> ReadAtLeastAsync(NetworkStream stream, int byteCount, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Max(byteCount * 2, 256)];
        var total = 0;

        while (total < byteCount)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
