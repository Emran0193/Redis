using FluentAssertions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using NovaDB.Contracts.Admin;
using System.Net;
using Xunit;

namespace IntegrationTests;

/// <summary>gRPC Admin API smoke against the hosted Server.</summary>
public sealed class AdminGrpcIntegrationTests : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _http = null!;
    private GrpcChannel _channel = null!;

    public Task InitializeAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "novadb-grpc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("environment", "Testing");
                builder.UseSetting("NovaDB:Port", "16380");
                builder.UseSetting("NovaDB:AofEnabled", "false");
                builder.UseSetting("NovaDB:DataDirectory", dataDir);
                builder.UseSetting("NovaDB:MaxConnections", "100");
            });

        _http = _factory.CreateDefaultClient(new Http2Handler());
        _channel = GrpcChannel.ForAddress(_http.BaseAddress!, new GrpcChannelOptions { HttpClient = _http });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _channel.Dispose();
        _http.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AdminGrpc_HealthMetricsKeys()
    {
        var health = new HealthService.HealthServiceClient(_channel);
        var metrics = new MetricsService.MetricsServiceClient(_channel);
        var keys = new KeyService.KeyServiceClient(_channel);

        (await health.GetHealthAsync(new Empty())).Ready.Should().BeTrue();
        await keys.SetStringAsync(new SetStringRequest { Key = "grpc:k", Value = "v", TtlMs = 30_000 });
        (await metrics.GetSnapshotAsync(new Empty())).KeyCount.Should().BeGreaterThanOrEqualTo(1);
        var scan = await keys.ScanKeysAsync(new ScanKeysRequest { Cursor = 0, Count = 40, Prefix = "grpc:" });
        scan.Keys.Should().Contain(k => k.Key == "grpc:k");
    }

    private sealed class Http2Handler : DelegatingHandler
    {
        public Http2Handler() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Version = request.Version;
            return response;
        }
    }
}
