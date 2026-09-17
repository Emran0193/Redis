using NovaDB.Protocol;

namespace NovaDB.Persistence.Aof;

/// <summary>
/// Replays a parsed RESP command array against the database during recovery.
/// </summary>
public interface IAofCommandReplayer
{
    /// <summary>
    /// Applies a mutating command during AOF replay.
    /// </summary>
    /// <param name="command">Parsed RESP command array.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ReplayAsync(RespValue command, CancellationToken cancellationToken);
}
