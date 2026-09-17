using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Chaos;
using NovaDB.Commands;
using NovaDB.Commands.Persistence;
using NovaDB.Configuration;
using NovaDB.Journal;
using NovaDB.Monitoring;
using NovaDB.Networking;
using NovaDB.Persistence;
using NovaDB.Persistence.Recovery;
using NovaDB.Protocol;
using NovaDB.PubSub;
using NovaDB.Replication;
using NovaDB.Server.Admin.Services;
using NovaDB.Server.Diagnostics;
using NovaDB.Storage;
using NovaDB.Transactions;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.ConfigureKestrel((context, options) =>
    {
        var section = context.Configuration.GetSection(NovaDbOptions.SectionName);
        var httpPort = section.GetValue("HttpPort", 7380);
        var grpcPort = section.GetValue("GrpcPort", 7381);

        // Cleartext: HTTP/1.1 and HTTP/2 cannot share a port (HTTP_1_1_REQUIRED).
        options.ListenLocalhost(httpPort, listen => listen.Protocols = HttpProtocols.Http1);
        options.ListenLocalhost(grpcPort, listen => listen.Protocols = HttpProtocols.Http2);
    });
}

builder.Services.AddNovaDbConfiguration(builder.Configuration);
builder.Services.AddNovaDbProtocol();
builder.Services.AddNovaDbStorage();
builder.Services.AddNovaDbPersistence();
builder.Services.AddNovaDbJournal();
builder.Services.AddNovaDbReplication();
builder.Services.AddNovaDbChaos();
builder.Services.AddNovaDbTransactions();
builder.Services.AddNovaDbPubSub();
builder.Services.AddNovaDbMonitoring();
builder.Services.AddNovaDbCommands();
builder.Services.AddNovaDbCommandReplay();
builder.Services.AddSingleton<MemoryDiagnosticsService>();
builder.Services.AddSingleton<ConnectionRateLimiter>();

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<TlsCertificateProvider>();
builder.Services.AddSingleton<IHostedService>(sp =>
{
    var gate = sp.GetRequiredService<DatabaseRecoveryGate>();
    return new TcpServerHostedService(
        sp.GetRequiredService<IOptions<NovaDbOptions>>(),
        sp.GetRequiredService<ConnectionManager>(),
        sp.GetRequiredService<ICommandProcessor>(),
        sp.GetRequiredService<ILogger<TcpServerHostedService>>(),
        sp.GetRequiredService<ILoggerFactory>(),
        sp.GetRequiredService<TlsCertificateProvider>(),
        sp.GetRequiredService<ConnectionRateLimiter>(),
        sp.GetService<IChaosFaultEngine>(),
        ct => gate.Ready.WaitAsync(ct));
});

builder.Services.AddGrpc();
builder.Services.AddHealthChecks()
    .AddCheck<NovaDbHealthCheck>("novadb");

var app = builder.Build();
NovaDB.Replication.ServiceCollectionExtensions.LogReplicationMode(app.Services);

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var status = report.Status.ToString();
        await context.Response.WriteAsJsonAsync(new
        {
            status,
            totalDuration = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description
            })
        }).ConfigureAwait(false);
    }
});

app.MapPrometheusScrapingEndpoint("/metrics");
app.MapGrpcService<MetricsGrpcService>();
app.MapGrpcService<KeyGrpcService>();
app.MapGrpcService<ClientGrpcService>();
app.MapGrpcService<ConfigGrpcService>();
app.MapGrpcService<HealthGrpcService>();
app.MapGrpcService<PersistenceGrpcService>();
app.MapGrpcService<PubSubGrpcService>();
app.MapGrpcService<HistoryGrpcService>();
app.MapGrpcService<ChaosGrpcService>();
app.MapGrpcService<DiagnosticsGrpcService>();
app.MapGrpcService<ReplicationGrpcService>();
app.MapGrpcService<PerformanceGrpcService>();

var options = app.Services.GetRequiredService<IOptions<NovaDbOptions>>().Value;

app.MapGet("/", () => Results.Ok(new
{
    name = "NovaDB",
    version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
    health = "/health",
    metrics = "/metrics",
    grpc = $"HTTP/2 cleartext on port {options.GrpcPort}"
}));

RespParser.Limits = new RespParseLimits
{
    MaxBulkBytes = options.MaxBulkBytes,
    MaxArrayLength = options.MaxArrayLength,
    MaxLineLength = options.MaxLineLength
};

app.Logger.LogInformation(
    "NovaDB starting. RESP {Port} on {Bind}, HTTP :{HttpPort}, gRPC :{GrpcPort}",
    options.Port,
    options.BindAddress,
    options.HttpPort,
    options.GrpcPort);

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Application entry point marker for integration tests.
/// </summary>
public partial class Program;
