namespace NovaDB.Replication;

/// <summary>
/// Credit-based flow-control window limiting in-flight replication bytes.
/// </summary>
public sealed class FlowControlWindow
{
    private readonly object _gate = new();
    private long _credits;
    private long _inFlightBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowControlWindow"/> class.
    /// </summary>
    /// <param name="initialCredits">Initial send credits in bytes.</param>
    public FlowControlWindow(long initialCredits = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCredits, 1);
        _credits = initialCredits;
    }

    /// <summary>Gets remaining send credits in bytes.</summary>
    public long Credits
    {
        get
        {
            lock (_gate)
            {
                return _credits;
            }
        }
    }

    /// <summary>Gets bytes currently in flight awaiting acknowledgment.</summary>
    public long InFlightBytes
    {
        get
        {
            lock (_gate)
            {
                return _inFlightBytes;
            }
        }
    }

    /// <summary>
    /// Attempts to reserve <paramref name="bytes"/> of send window.
    /// </summary>
    /// <returns><see langword="true"/> when credits were available.</returns>
    public bool TryAcquire(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        lock (_gate)
        {
            if (_credits < bytes)
            {
                return false;
            }

            _credits -= bytes;
            _inFlightBytes += bytes;
            return true;
        }
    }

    /// <summary>
    /// Releases in-flight bytes and restores credits when the peer acknowledges.
    /// </summary>
    public void Release(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        lock (_gate)
        {
            var release = Math.Min(bytes, _inFlightBytes);
            _inFlightBytes -= release;
            _credits += release;
        }
    }

    /// <summary>Adds additional credits (window update from peer).</summary>
    public void Grant(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        lock (_gate)
        {
            _credits += bytes;
        }
    }
}
