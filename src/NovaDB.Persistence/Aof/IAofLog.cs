namespace NovaDB.Persistence.Aof;

/// <summary>
/// Append-only command log persisted on disk.
/// </summary>
public interface IAofLog
{
    /// <summary>
    /// Appends a RESP-serialized mutating command to the log.
    /// </summary>
    /// <param name="respBytes">Serialized RESP command array.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AppendAsync(ReadOnlyMemory<byte> respBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Forces any buffered writes to disk.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rewrites the AOF from the live dataset so the log stays bounded after a snapshot.
    /// </summary>
    /// <param name="storage">Storage engine to encode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Waits if another rewrite is already running. Prefer
    /// <see cref="TryScheduleBackgroundRewrite"/> for the client-facing BGREWRITEAOF path.
    /// </remarks>
    Task RewriteFromStorageAsync(NovaDB.Storage.IStorageEngine storage, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to start a background rewrite without blocking the caller.
    /// </summary>
    /// <param name="storage">Storage engine to encode.</param>
    /// <param name="cancellationToken">Cancellation token for the rewrite work.</param>
    /// <returns>
    /// <see langword="false"/> when AOF is disabled or a rewrite is already in progress;
    /// otherwise <see langword="true"/> and the rewrite runs asynchronously.
    /// </returns>
    bool TryScheduleBackgroundRewrite(NovaDB.Storage.IStorageEngine storage, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the path to the active AOF file.
    /// </summary>
    string FilePath { get; }
}
