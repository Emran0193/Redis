using NovaDB.Core.Models;
using NovaDB.Storage;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Persists and restores NovaDB snapshots on disk.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Gets a value indicating whether a snapshot file is present on disk.</summary>
    bool SnapshotExists { get; }

    /// <summary>
    /// Writes a point-in-time snapshot of all keys in the storage engine.
    /// </summary>
    /// <param name="storage">Storage engine to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(IStorageEngine storage, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the latest snapshot entries from disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recovered entries, or an empty collection when no snapshot exists.</returns>
    Task<IReadOnlyList<(RedisKey Key, DatabaseEntry Entry)>> LoadLatestAsync(CancellationToken cancellationToken);
}
