using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaDB.Configuration;
using NovaDB.Persistence.Aof;
using NovaDB.Persistence.Snapshot;
using NovaDB.Storage;

namespace NovaDB.Persistence.Recovery;

/// <summary>
/// Hosted service that restores the database from snapshot and AOF before serving clients.
/// </summary>
public sealed class DatabaseRecoveryService : IHostedService
{
    private readonly IStorageEngine _storage;
    private readonly ISnapshotStore _snapshotStore;
    private readonly AofReplayEngine _aofReplayEngine;
    private readonly AofWriter _aofWriter;
    private readonly DatabaseRecoveryGate _recoveryGate;
    private readonly NovaDbOptions _options;
    private readonly ILogger<DatabaseRecoveryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseRecoveryService"/> class.
    /// </summary>
    /// <param name="storage">Storage engine to populate.</param>
    /// <param name="snapshotStore">Snapshot store.</param>
    /// <param name="aofReplayEngine">AOF replay engine.</param>
    /// <param name="aofWriter">AOF writer providing the log path.</param>
    /// <param name="recoveryGate">Recovery readiness gate.</param>
    /// <param name="options">Database options.</param>
    /// <param name="logger">Logger instance.</param>
    public DatabaseRecoveryService(
        IStorageEngine storage,
        ISnapshotStore snapshotStore,
        AofReplayEngine aofReplayEngine,
        AofWriter aofWriter,
        DatabaseRecoveryGate recoveryGate,
        Microsoft.Extensions.Options.IOptions<NovaDbOptions> options,
        ILogger<DatabaseRecoveryService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _aofReplayEngine = aofReplayEngine ?? throw new ArgumentNullException(nameof(aofReplayEngine));
        _aofWriter = aofWriter ?? throw new ArgumentNullException(nameof(aofWriter));
        _recoveryGate = recoveryGate ?? throw new ArgumentNullException(nameof(recoveryGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Starting database recovery.");

            // Exactly one source is applied. Replaying the AOF on top of a snapshot would
            // re-apply every append-style command the snapshot already contains (LPUSH, SADD,
            // HSET, ZADD), silently corrupting the dataset, so the AOF wins whenever it holds
            // data and the snapshot is only used as the fallback.
            if (HasReplayableAof())
            {
                var replayed = await _aofReplayEngine
                    .ReplayAsync(_aofWriter.FilePath, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Database recovery completed from AOF. AofCommands={AofCommands}",
                    replayed);
            }
            else
            {
                var entries = await _snapshotStore.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
                foreach (var (key, entry) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _storage.SetAsync(key, entry, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "Database recovery completed from snapshot. SnapshotEntries={SnapshotEntries}",
                    entries.Count);
            }

            _recoveryGate.MarkReady();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database recovery failed.");
            _recoveryGate.MarkFailed(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private bool HasReplayableAof()
    {
        if (!_options.AofEnabled)
        {
            return false;
        }

        var file = new FileInfo(_aofWriter.FilePath);
        if (!file.Exists || file.Length == 0)
        {
            return false;
        }

        if (_snapshotStore.SnapshotExists)
        {
            _logger.LogWarning(
                "Both an AOF and a snapshot are present. Recovering from the AOF and ignoring the snapshot; " +
                "rewrite the AOF from the live dataset if it was enabled after the snapshot was taken.");
        }

        return true;
    }
}
