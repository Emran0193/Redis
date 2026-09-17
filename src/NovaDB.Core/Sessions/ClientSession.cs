using NovaDB.Core.Models;

namespace NovaDB.Core.Sessions;

/// <summary>
/// Mutable per-connection session state for authentication, transactions, WATCH, and pub/sub.
/// Rare collections (MULTI queue, WATCH, subscriptions) are allocated lazily.
/// </summary>
public sealed class ClientSession
{
    private List<object>? _queuedCommands;
    private Dictionary<RedisKey, long>? _watchedKeyVersions;
    private HashSet<string>? _subscriptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientSession"/> class.
    /// </summary>
    /// <param name="connectionId">Unique connection identifier.</param>
    public ClientSession(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ConnectionId = connectionId;
    }

    /// <summary>Gets the unique connection identifier.</summary>
    public string ConnectionId { get; }

    /// <summary>Gets or sets whether the client has authenticated.</summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets whether a MULTI transaction is active.</summary>
    public bool InMulti { get; set; }

    /// <summary>
    /// Gets commands queued between MULTI and EXEC.
    /// The commands layer stores parsed RESP command values in this list.
    /// </summary>
    public List<object> QueuedCommands => _queuedCommands ??= [];

    /// <summary>Gets watched key versions for optimistic concurrency (binary-safe keys).</summary>
    public Dictionary<RedisKey, long> WatchedKeyVersions
        => _watchedKeyVersions ??= new(RedisKeyComparer.Instance);

    /// <summary>Gets subscribed pub/sub channel names.</summary>
    public HashSet<string> Subscriptions => _subscriptions ??= new(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether the client is in pub/sub mode.</summary>
    public bool IsPubSubMode => _subscriptions is { Count: > 0 };

    /// <summary>Gets or sets whether the client requested disconnect (QUIT).</summary>
    public bool ShouldClose { get; set; }

    /// <summary>Gets or sets the optional client name from CLIENT SETNAME.</summary>
    public string? ClientName { get; set; }

    /// <summary>Gets or sets lib name from CLIENT SETINFO.</summary>
    public string? LibraryName { get; set; }

    /// <summary>Gets or sets lib version from CLIENT SETINFO.</summary>
    public string? LibraryVersion { get; set; }

    /// <summary>
    /// Clears transaction state including MULTI mode, queued commands, and watched keys.
    /// </summary>
    public void ClearTransaction()
    {
        InMulti = false;
        _queuedCommands?.Clear();
        _watchedKeyVersions?.Clear();
    }

    /// <summary>Clears WATCH state without allocating if none were watched.</summary>
    public void ClearWatches() => _watchedKeyVersions?.Clear();
}
