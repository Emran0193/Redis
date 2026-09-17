using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using FluentAssertions;
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

public sealed class TlsIntegrationTests
{
    [Fact]
    public async Task TlsPing_ReturnsPong()
    {
        var certPath = Path.Combine(Path.GetTempPath(), "novadb-tls-" + Guid.NewGuid().ToString("N") + ".pfx");
        CreateSelfSignedPfx(certPath, "test");

        var port = GetFreeTcpPort();
        var dataDir = Path.Combine(Path.GetTempPath(), "novadb-tls-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NovaDB:Port"] = port.ToString(),
            ["NovaDB:BindAddress"] = "127.0.0.1",
            ["NovaDB:AofEnabled"] = "false",
            ["NovaDB:DataDirectory"] = dataDir,
            ["NovaDB:SnapshotInterval"] = "01:00:00",
            ["NovaDB:IdleTimeout"] = "00:01:00",
            ["NovaDB:MaxConnections"] = "100",
            ["NovaDB:ShardCount"] = "4",
            ["NovaDB:TlsEnabled"] = "true",
            ["NovaDB:TlsCertificatePath"] = certPath,
            ["NovaDB:TlsCertificatePassword"] = "test"
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

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var service in hosted)
        {
            await service.StartAsync(cts.Token);
        }

        try
        {
            await Task.Delay(200, cts.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "localhost",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            }, cts.Token);

            await ssl.WriteAsync(Encoding.UTF8.GetBytes("*1\r\n$4\r\nPING\r\n"), cts.Token);
            var buffer = new byte[64];
            var read = await ssl.ReadAsync(buffer, cts.Token);
            Encoding.UTF8.GetString(buffer, 0, read).Should().Be("+PONG\r\n");
        }
        finally
        {
            foreach (var service in hosted)
            {
                try { await service.StopAsync(CancellationToken.None); }
                catch (OperationCanceledException) { }
            }

            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }

            if (File.Exists(certPath))
            {
                File.Delete(certPath);
            }
        }
    }

    private static void CreateSelfSignedPfx(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
