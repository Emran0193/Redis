namespace NovaDB.Persistence.Aof;

/// <summary>
/// Thrown when the append-only log cannot be replayed faithfully.
/// </summary>
/// <remarks>
/// Recovery is fail-closed: rather than starting with a partially applied dataset, the server
/// refuses to start so the operator can restore from a snapshot or repair the log.
/// </remarks>
public sealed class AofReplayException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AofReplayException"/> class.
    /// </summary>
    /// <param name="message">Failure description.</param>
    public AofReplayException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AofReplayException"/> class.
    /// </summary>
    /// <param name="message">Failure description.</param>
    /// <param name="innerException">Underlying failure.</param>
    public AofReplayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
