using NovaDB.Core.Models;

namespace NovaDB.Storage.Expiration;

/// <summary>
/// Schedules and cancels absolute key expirations.
/// </summary>
public interface IExpirationScheduler
{
    /// <summary>
    /// Schedules a key for expiration at the given Unix millisecond timestamp.
    /// </summary>
    /// <param name="key">Key to expire.</param>
    /// <param name="expireAtUnixMs">Absolute expiration timestamp.</param>
    void Schedule(RedisKey key, long expireAtUnixMs);

    /// <summary>
    /// Cancels a previously scheduled expiration.
    /// </summary>
    /// <param name="key">Key to cancel.</param>
    void Cancel(RedisKey key);
}
