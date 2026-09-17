namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Constants for the NovaDB binary snapshot format.
/// </summary>
internal static class SnapshotFormat
{
    /// <summary>Current snapshot format version.</summary>
    public const ushort CurrentVersion = 1;

    /// <summary>Flag indicating the entries payload is GZip-compressed.</summary>
    public const ushort FlagCompressed = 0x0001;

    /// <summary>On-disk snapshot file name within the data directory.</summary>
    public const string SnapshotFileName = "snapshot.novdb";

    /// <summary>Temporary snapshot file name used during atomic writes.</summary>
    public const string SnapshotTempFileName = "snapshot.novdb.tmp";

    /// <summary>
    /// Returns the path for generation <paramref name="generation"/> (0 = current).
    /// </summary>
    /// <param name="dataDirectory">Data directory.</param>
    /// <param name="generation">0 for the live snapshot, 1+ for older backups.</param>
    public static string GetSnapshotPath(string dataDirectory, int generation)
        => generation <= 0
            ? Path.Combine(dataDirectory, SnapshotFileName)
            : Path.Combine(dataDirectory, $"{SnapshotFileName}.{generation}");

    /// <summary>Magic header bytes written at the start of every snapshot file.</summary>
    public static ReadOnlySpan<byte> Magic => "NOVADB\n"u8;
}
