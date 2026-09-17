namespace NovaDB.Protocol;

/// <summary>
/// Bounds for RESP parsing to prevent attacker-controlled allocations.
/// </summary>
public sealed class RespParseLimits
{
    /// <summary>Default limits (16 MiB bulk, 1M array elements, 64 KiB lines).</summary>
    public static RespParseLimits Default { get; } = new();

    /// <summary>Gets or sets the maximum bulk string payload in bytes.</summary>
    public int MaxBulkBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Gets or sets the maximum number of elements in a RESP array.</summary>
    public int MaxArrayLength { get; set; } = 1_000_000;

    /// <summary>Gets or sets the maximum simple/integer/length line in bytes.</summary>
    public int MaxLineLength { get; set; } = 64 * 1024;
}
