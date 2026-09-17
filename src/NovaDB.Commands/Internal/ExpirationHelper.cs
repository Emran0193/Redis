using NovaDB.Core.Models;
using NovaDB.Storage;
using NovaDB.Storage.Expiration;

namespace NovaDB.Commands.Internal;

/// <summary>
/// Applies expiration metadata to storage entries and optional scheduler.
/// </summary>
internal static class ExpirationHelper
{
    /// <summary>
    /// Sets absolute expiration on an entry and notifies the scheduler when registered.
    /// </summary>
    public static void ApplyExpiration(
        DatabaseEntry entry,
        RedisKey key,
        long? expireAtUnixMs,
        IExpirationScheduler? scheduler)
    {
        entry.ExpireAtUnixMs = expireAtUnixMs;

        if (scheduler is null)
        {
            return;
        }

        if (expireAtUnixMs is long at)
        {
            scheduler.Schedule(key, at);
        }
        else
        {
            scheduler.Cancel(key);
        }
    }

    /// <summary>
    /// Removes expiration from an entry.
    /// </summary>
    public static void ClearExpiration(DatabaseEntry entry, RedisKey key, IExpirationScheduler? scheduler)
    {
        entry.ExpireAtUnixMs = null;
        scheduler?.Cancel(key);
    }
}
