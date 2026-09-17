namespace NovaDB.Configuration;

/// <summary>
/// Root configuration for the NovaDB server bound from appsettings.json.
/// </summary>
public sealed class NovaDbOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "NovaDB";

    /// <summary>Gets or sets the TCP listen port for RESP clients.</summary>
    public int Port { get; set; } = 6379;

    /// <summary>Gets or sets the HTTP/1.1 port for health and metrics.</summary>
    public int HttpPort { get; set; } = 7380;

    /// <summary>
    /// Gets or sets the cleartext HTTP/2 port for Admin gRPC.
    /// Must be separate from <see cref="HttpPort"/> — Kestrel cannot multiplex HTTP/1.1 and HTTP/2 without TLS.
    /// </summary>
    public int GrpcPort { get; set; } = 7381;

    /// <summary>Gets or sets the maximum concurrent TCP connections.</summary>
    public int MaxConnections { get; set; } = 10_000;

    /// <summary>Gets or sets the memory limit in bytes (0 = unlimited).</summary>
    public long MemoryLimitBytes { get; set; }

    /// <summary>Gets or sets the eviction policy name: noeviction, lru, lfu, ttl.</summary>
    public string EvictionPolicy { get; set; } = "noeviction";

    /// <summary>Gets or sets the snapshot interval.</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets whether AOF persistence is enabled.</summary>
    public bool AofEnabled { get; set; } = true;

    /// <summary>Gets or sets the AOF flush policy: always, everysec, no.</summary>
    public string AofFlushPolicy { get; set; } = "everysec";

    /// <summary>
    /// Gets or sets the maximum number of AOF records buffered before writers are throttled.
    /// </summary>
    public int AofWriteQueueCapacity { get; set; } = 65_536;

    /// <summary>Gets or sets the data directory for RDB/AOF files.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Gets or sets whether snapshot compression is enabled.</summary>
    public bool SnapshotCompression { get; set; }

    /// <summary>
    /// Gets or sets how many previous snapshot generations to keep for CRC-failure fallback.
    /// </summary>
    public int SnapshotGenerationCount { get; set; } = 3;

    /// <summary>
    /// Gets or sets the base lockout duration after a failed AUTH before retries are allowed again.
    /// Doubles on each subsequent failure up to <see cref="AuthLockoutMax"/>.
    /// </summary>
    public TimeSpan AuthLockoutBase { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the maximum AUTH lockout duration.
    /// </summary>
    public TimeSpan AuthLockoutMax { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets the AUTH password. Empty disables authentication.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets idle connection timeout.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Gets or sets the number of hash-slot shards (must be power of two recommended).</summary>
    public int ShardCount { get; set; } = 16;

    /// <summary>Gets or sets bind address.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>
    /// Gets or sets whether TLS is required for RESP client connections.
    /// </summary>
    public bool TlsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the path to a PFX certificate used when <see cref="TlsEnabled"/> is true.
    /// </summary>
    public string TlsCertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the password for <see cref="TlsCertificatePath"/>, if any.
    /// </summary>
    public string TlsCertificatePassword { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum RESP bulk string payload in bytes (DoS guard).
    /// </summary>
    public int MaxBulkBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum RESP array element count (DoS guard).
    /// </summary>
    public int MaxArrayLength { get; set; } = 1_000_000;

    /// <summary>
    /// Gets or sets the maximum RESP header/simple line length in bytes.
    /// </summary>
    public int MaxLineLength { get; set; } = 64 * 1024;

    /// <summary>
    /// Gets or sets whether binding to a non-loopback address without AUTH (and without TLS)
    /// is allowed. Defaults to <see langword="false"/> (fail startup).
    /// </summary>
    public bool AllowUnauthenticatedPublicBind { get; set; }

    /// <summary>
    /// Returns true when a password is configured.
    /// </summary>
    public bool AuthenticationRequired => !string.IsNullOrEmpty(Password);
}
