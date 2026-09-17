using Microsoft.Extensions.DependencyInjection;
using NovaDB.Persistence.Aof;
using NovaDB.Persistence.Recovery;
using NovaDB.Persistence.Snapshot;

namespace NovaDB.Persistence;

/// <summary>
/// Dependency injection extensions for NovaDB persistence.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers snapshot storage, append-only logging, recovery, and background snapshot services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// AOF recovery replays records through the command handlers, which this layer cannot reference.
    /// Callers must also call <c>AddNovaDbCommandReplay</c> to supply an
    /// <see cref="IAofCommandReplayer"/>.
    /// </remarks>
    public static IServiceCollection AddNovaDbPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<DatabaseRecoveryGate>();
        services.AddSingleton<NovaDB.Core.Hosting.IDatabaseReadiness>(sp => sp.GetRequiredService<DatabaseRecoveryGate>());
        services.AddSingleton<IAofMetrics>(_ => NullAofMetrics.Instance);
        services.AddSingleton<SnapshotWriter>();
        services.AddSingleton<SnapshotReader>();
        services.AddSingleton<ISnapshotStore, FileSnapshotStore>();
        services.AddSingleton<AofReplayEngine>();
        services.AddSingleton<AofWriter>();
        services.AddSingleton<IAofLog>(sp => sp.GetRequiredService<AofWriter>());
        services.AddSingleton<ICommandMutationSink>(sp => sp.GetRequiredService<AofWriter>());

        // The writer starts before recovery so the log is ready to accept appends by the time the
        // recovery gate lets clients in. Resolve the singleton rather than letting the host
        // construct its own AofWriter: a second instance would be the one that gets started,
        // leaving the instance used as the mutation sink with no open file and no drain loop, so
        // nothing would ever reach the log.
        services.AddHostedService(sp => sp.GetRequiredService<AofWriter>());
        services.AddHostedService<DatabaseRecoveryService>();
        services.AddHostedService<SnapshotBackgroundService>();

        return services;
    }
}
