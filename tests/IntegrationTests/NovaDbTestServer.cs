using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Commands;
using NovaDB.Commands.Persistence;
using NovaDB.Configuration;
using NovaDB.Networking;
using NovaDB.Persistence;
using NovaDB.Persistence.Recovery;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Transactions;

namespace IntegrationTests;

/// <summary>
/// Starts a real NovaDB TCP server on a free loopback port for end-to-end tests.
/// </summary>
public sealed class NovaDbTestServer : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly List<IHostedService> _hostedServices;
    private readonly string _dataDirectory;

    private NovaDbTestServer(ServiceProvider provider, List<IHostedService> hostedServices, int port, string dataDirectory)
    {
        _provider = provider;
        _hostedServices = hostedServices;
        _dataDirectory = dataDirectory;
        Port = port;
    }

    /// <summary>Gets the RESP port the server is listening on.</summary>
    public int Port { get; }

    /// <summary>
    /// Starts a server and waits until it accepts connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<NovaDbTestServer> StartAsync(CancellationToken cancellationToken)
    {
        var port = GetFreeTcpPort();
        var dataDirectory = Path.Combine(Path.GetTempPath(), "novadb-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NovaDB:Port"] = port.ToString(),
            ["NovaDB:BindAddress"] = "127.0.0.1",
            ["NovaDB:AofEnabled"] = "false",
            ["NovaDB:DataDirectory"] = dataDirectory,
            ["NovaDB:SnapshotInterval"] = "01:00:00",
            ["NovaDB:IdleTimeout"] = "00:01:00",
            ["NovaDB:MaxConnections"] = "100",
            ["NovaDB:ShardCount"] = "4"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddNovaDbConfiguration(configuration);
        services.AddNovaDbStorage();
        services.AddNovaDbPersistence();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();
        services.AddNovaDbCommands();
        services.AddNovaDbCommandReplay();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<TlsCertificateProvider>();
        services.AddSingleton<IHostedService>(sp =>
        {
            var gate = sp.GetRequiredService<DatabaseRecoveryGate>();
            gate.MarkReady();
            return new TcpServerHostedService(
                sp.GetRequiredService<IOptions<NovaDbOptions>>(),
                sp.GetRequiredService<ConnectionManager>(),
                sp.GetRequiredService<ICommandProcessor>(),
                sp.GetRequiredService<ILogger<TcpServerHostedService>>(),
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<TlsCertificateProvider>());
        });

        var provider = services.BuildServiceProvider();
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        foreach (var service in hostedServices)
        {
            await service.StartAsync(cancellationToken);
        }

        await WaitUntilListeningAsync(port, cancellationToken);
        return new NovaDbTestServer(provider, hostedServices, port, dataDirectory);
    }

    /// <summary>
    /// Opens a connected client socket stream against the server.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<TcpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, Port, cancellationToken);
        return client;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var service in _hostedServices)
        {
            try
            {
                await service.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Background hosted services may already be stopping.
            }
        }

        await _provider.DisposeAsync();

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private static async Task WaitUntilListeningAsync(int port, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new InvalidOperationException($"NovaDB test server did not start listening on port {port}.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
