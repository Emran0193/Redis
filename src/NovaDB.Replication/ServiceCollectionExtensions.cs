using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Journal;

namespace NovaDB.Replication;

/// <summary>
/// DI registration for replication foundation services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers replication foundation types (offset tracker, streamer, snapshot handshake, transport stubs).
    /// Respects <see cref="NovaDbOptions.ReadOnlyReplica"/> — when true, mutating commands are already
    /// rejected by the command dispatcher; this registration still wires streaming primitives so a
    /// replica process can consume journal traffic later.
    /// Call after <c>AddNovaDbJournal()</c>.
    /// </summary>
    public static IServiceCollection AddNovaDbReplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ReplicationOffsetTracker>();
        services.TryAddSingleton<ICommandStreamer>(sp =>
            new InMemoryCommandStreamer(sp.GetRequiredService<ICommandJournal>()));
        services.TryAddSingleton<IReplicationSnapshot>(sp =>
        {
            var journal = sp.GetRequiredService<ICommandJournal>();
            return SnapshotHandshake.FromJournalOffset(journal.CurrentOffset);
        });
        services.TryAddSingleton<TcpReplicationTransport>();
        services.TryAddSingleton<IReplicationTransport>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<NovaDbOptions>>().Value;
            // Foundation: use TCP stub when read-only replica; otherwise null transport.
            return options.ReadOnlyReplica
                ? sp.GetRequiredService<TcpReplicationTransport>()
                : NullReplicationTransport.Instance;
        });

        return services;
    }

    /// <summary>
    /// Logs the replication mode once the host builds (optional helper for server startup).
    /// </summary>
    public static void LogReplicationMode(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.GetRequiredService<IOptions<NovaDbOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("NovaDB.Replication");
        if (options.ReadOnlyReplica)
        {
            logger.LogInformation("NovaDB replication registered in read-only replica mode");
        }
        else
        {
            logger.LogInformation("NovaDB replication foundation registered (primary mode)");
        }
    }
}
