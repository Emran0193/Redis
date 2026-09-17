namespace NovaDB.Replication;

/// <summary>
/// Default <see cref="IReplicaSession"/> holding reconnect token and last ack offset.
/// </summary>
public sealed class ReplicaSession : IReplicaSession
{
    private readonly object _gate = new();
    private string _reconnectToken;
    private ulong _lastAckOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicaSession"/> class.
    /// </summary>
    public ReplicaSession(string replicaId, string? reconnectToken = null, ulong lastAckOffset = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicaId);
        ReplicaId = replicaId;
        _reconnectToken = string.IsNullOrWhiteSpace(reconnectToken)
            ? Guid.NewGuid().ToString("N")
            : reconnectToken;
        _lastAckOffset = lastAckOffset;
    }

    /// <inheritdoc />
    public string ReplicaId { get; }

    /// <inheritdoc />
    public string ReconnectToken
    {
        get
        {
            lock (_gate)
            {
                return _reconnectToken;
            }
        }
    }

    /// <inheritdoc />
    public ulong LastAckOffset
    {
        get
        {
            lock (_gate)
            {
                return _lastAckOffset;
            }
        }
    }

    /// <inheritdoc />
    public void Acknowledge(ulong offset)
    {
        lock (_gate)
        {
            if (offset > _lastAckOffset)
            {
                _lastAckOffset = offset;
            }
        }
    }

    /// <inheritdoc />
    public string RotateReconnectToken()
    {
        lock (_gate)
        {
            _reconnectToken = Guid.NewGuid().ToString("N");
            return _reconnectToken;
        }
    }
}
