using NovaDB.Core.Models;
using NovaDB.Storage;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// File-backed implementation of <see cref="ISnapshotStore"/>.
/// </summary>
public sealed class FileSnapshotStore : ISnapshotStore
{
    private readonly SnapshotWriter _writer;
    private readonly SnapshotReader _reader;
    private readonly string _snapshotPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileSnapshotStore"/> class.
    /// </summary>
    /// <param name="writer">Snapshot writer.</param>
    /// <param name="reader">Snapshot reader.</param>
    /// <param name="options">Database options.</param>
    public FileSnapshotStore(
        SnapshotWriter writer,
        SnapshotReader reader,
        Microsoft.Extensions.Options.IOptions<NovaDB.Configuration.NovaDbOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _snapshotPath = Path.Combine(options.Value.DataDirectory, SnapshotFormat.SnapshotFileName);
    }

    /// <inheritdoc />
    public bool SnapshotExists => File.Exists(_snapshotPath);

    /// <inheritdoc />
    public Task SaveAsync(IStorageEngine storage, CancellationToken cancellationToken)
        => _writer.WriteAsync(storage, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<(RedisKey Key, DatabaseEntry Entry)>> LoadLatestAsync(CancellationToken cancellationToken)
        => _reader.ReadLatestAsync(cancellationToken);
}
