using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Persistence.Aof;
using NovaDB.Persistence.Recovery;
using NovaDB.Persistence.Snapshot;
using NovaDB.Storage;

namespace NovaDB.Persistence;

/// <summary>
/// Periodically writes snapshots according to <see cref="NovaDbOptions.SnapshotInterval"/>.
/// </summary>
public sealed class SnapshotBackgroundService : BackgroundService
{
    private readonly NovaDbOptions _options;
    private readonly IStorageEngine _storage;
    private readonly ISnapshotStore _snapshotStore;
    private readonly IAofLog _aofLog;
    private readonly DatabaseRecoveryGate _recoveryGate;
    private readonly ILogger<SnapshotBackgroundService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotBackgroundService"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <param name="storage">Storage engine to snapshot.</param>
    /// <param name="snapshotStore">Snapshot store.</param>
    /// <param name="aofLog">Append-only log rewritten after each successful snapshot.</param>
    /// <param name="recoveryGate">Recovery readiness gate.</param>
    /// <param name="logger">Logger instance.</param>
    public SnapshotBackgroundService(
        IOptions<NovaDbOptions> options,
        IStorageEngine storage,
        ISnapshotStore snapshotStore,
        IAofLog aofLog,
        DatabaseRecoveryGate recoveryGate,
        ILogger<SnapshotBackgroundService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
        _aofLog = aofLog ?? throw new ArgumentNullException(nameof(aofLog));
        _recoveryGate = recoveryGate ?? throw new ArgumentNullException(nameof(recoveryGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _recoveryGate.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

        if (_options.SnapshotInterval <= TimeSpan.Zero)
        {
            _logger.LogInformation("Periodic snapshots are disabled because SnapshotInterval is not positive.");
            return;
        }

        using var timer = new PeriodicTimer(_options.SnapshotInterval);

        _logger.LogInformation(
            "Snapshot background service started with interval {SnapshotInterval}.",
            _options.SnapshotInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await WriteSnapshotSafelyAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }

    private async Task WriteSnapshotSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _snapshotStore.SaveAsync(_storage, cancellationToken).ConfigureAwait(false);
            await _aofLog.RewriteFromStorageAsync(_storage, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Periodic snapshot failed.");
        }
    }
}
