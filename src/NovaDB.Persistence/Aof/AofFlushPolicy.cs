namespace NovaDB.Persistence.Aof;

/// <summary>
/// Append-only file flush behavior.
/// </summary>
public enum AofFlushPolicy
{
    /// <summary>Flush to disk after every append.</summary>
    Always,

    /// <summary>Flush at most once per second.</summary>
    EverySec,

    /// <summary>Let the operating system buffer writes.</summary>
    No
}

/// <summary>
/// Parses configured AOF flush policy strings.
/// </summary>
public static class AofFlushPolicyParser
{
    /// <summary>
    /// Parses a flush policy name from configuration.
    /// </summary>
    /// <param name="value">Policy name: always, everysec, or no.</param>
    /// <returns>Parsed flush policy.</returns>
    public static AofFlushPolicy Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        return value.Trim().ToLowerInvariant() switch
        {
            "always" => AofFlushPolicy.Always,
            "everysec" => AofFlushPolicy.EverySec,
            "no" => AofFlushPolicy.No,
            _ => throw new InvalidOperationException($"Unsupported AOF flush policy: {value}")
        };
    }
}
