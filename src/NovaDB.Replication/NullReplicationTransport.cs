namespace NovaDB.Replication;

/// <summary>
/// No-op transport used when replication networking is disabled.
/// </summary>
public sealed class NullReplicationTransport : IReplicationTransport
{
    /// <summary>Shared singleton instance.</summary>
    public static NullReplicationTransport Instance { get; } = new();

    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
