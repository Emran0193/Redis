using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaDB.Commands;
using NovaDB.Commands.Persistence;
using NovaDB.Configuration;
using NovaDB.Persistence;
using NovaDB.PubSub;
using NovaDB.Storage;
using NovaDB.Transactions;

namespace UnitTests.Helpers;

/// <summary>
/// Builds a test <see cref="ServiceProvider"/> with NovaDB command and storage services.
/// </summary>
public static class TestHostFactory
{
    /// <summary>
    /// Creates a configured service provider and starts hosted services (expiration scheduler).
    /// </summary>
    public static ServiceProvider Create(Action<NovaDbOptions>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:Port"] = "6379",
                [$"{NovaDbOptions.SectionName}:MaxConnections"] = "100",
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "4",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction",
                [$"{NovaDbOptions.SectionName}:AofEnabled"] = "false",
                [$"{NovaDbOptions.SectionName}:DataDirectory"] = Path.Combine(Path.GetTempPath(), "novadb-tests"),
                [$"{NovaDbOptions.SectionName}:Password"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None).AddProvider(new NullLoggerProvider()));
        services.AddNovaDbConfiguration(configuration);

        if (configure is not null)
        {
            services.PostConfigure(configure);
        }

        services.AddNovaDbStorage();
        services.AddNovaDbCommands();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();

        var provider = services.BuildServiceProvider();
        StartHostedServices(provider);
        return provider;
    }

    /// <summary>
    /// Creates a provider with persistence enabled against a real data directory, so that
    /// recovery (snapshot load and AOF replay) runs during startup.
    /// </summary>
    /// <param name="dataDirectory">Directory holding the AOF and snapshot files.</param>
    /// <param name="aofFlushPolicy">AOF flush policy: always, everysec, or no.</param>
    public static ServiceProvider CreatePersistent(string dataDirectory, string aofFlushPolicy = "always")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "4",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction",
                [$"{NovaDbOptions.SectionName}:AofEnabled"] = "true",
                [$"{NovaDbOptions.SectionName}:AofFlushPolicy"] = aofFlushPolicy,
                [$"{NovaDbOptions.SectionName}:SnapshotInterval"] = "01:00:00",
                [$"{NovaDbOptions.SectionName}:DataDirectory"] = dataDirectory,
                [$"{NovaDbOptions.SectionName}:Password"] = string.Empty
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None).AddProvider(new NullLoggerProvider()));
        services.AddNovaDbConfiguration(configuration);
        services.AddNovaDbStorage();
        services.AddNovaDbPersistence();
        services.AddNovaDbTransactions();
        services.AddNovaDbPubSub();
        services.AddNovaDbCommands();
        services.AddNovaDbCommandReplay();

        var provider = services.BuildServiceProvider();
        try
        {
            StartHostedServices(provider);
        }
        catch
        {
            // Leave no started services behind holding the data files open.
            DisposeAsync(provider).GetAwaiter().GetResult();
            throw;
        }

        return provider;
    }

    /// <summary>
    /// Creates a storage-only provider for engine-level tests.
    /// </summary>
    public static ServiceProvider CreateStorage(Action<NovaDbOptions>? configure = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{NovaDbOptions.SectionName}:ShardCount"] = "4",
                [$"{NovaDbOptions.SectionName}:MemoryLimitBytes"] = "0",
                [$"{NovaDbOptions.SectionName}:EvictionPolicy"] = "noeviction"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None).AddProvider(new NullLoggerProvider()));
        services.AddNovaDbConfiguration(configuration);

        if (configure is not null)
        {
            services.PostConfigure(configure);
        }

        services.AddNovaDbStorage();

        var provider = services.BuildServiceProvider();
        StartHostedServices(provider);
        return provider;
    }

    /// <summary>
    /// Stops and disposes hosted services and the provider.
    /// </summary>
    public static async Task DisposeAsync(ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>().Reverse())
        {
            try
            {
                await hosted.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Background timers may already be stopping.
            }
        }

        await provider.DisposeAsync();
    }

    private static void StartHostedServices(ServiceProvider provider)
    {
        foreach (var hosted in provider.GetServices<IHostedService>())
        {
            hosted.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
    }

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }
}
