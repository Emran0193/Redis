using System.Text;
using FluentAssertions;

namespace IntegrationTests;

public sealed class PingIntegrationTests
{
    [Fact]
    public async Task TcpPing_ReturnsPong()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = await NovaDbTestServer.StartAsync(cts.Token);
        using var client = await server.ConnectAsync(cts.Token);

        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes("*1\r\n$4\r\nPING\r\n"), cts.Token);

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, cts.Token);

        Encoding.UTF8.GetString(buffer, 0, read).Should().Be("+PONG\r\n");
    }
}
