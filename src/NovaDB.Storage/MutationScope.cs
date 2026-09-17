using NovaDB.Core.Models;

namespace NovaDB.Storage;

/// <summary>
/// Describes the change a mutation delegate applied to a key.
/// </summary>
public enum MutationOutcome
{
    /// <summary>The key was left untouched.</summary>
    None = 0,

    /// <summary>The existing value object was mutated in place.</summary>
    InPlace = 1,

    /// <summary>The entry was replaced by a new one.</summary>
    Replaced = 2,

    /// <summary>The key was removed.</summary>
    Removed = 3
}

/// <summary>
/// Read-modify-write handle passed to a mutation delegate while the owning shard lock is held.
/// </summary>
/// <remarks>
/// The delegate runs inside the shard lock, so it must stay synchronous, short, and must never
/// call back into <see cref="IStorageEngine"/>.
/// </remarks>
public sealed class MutationScope
{
    internal MutationScope(DatabaseEntry? entry) => Entry = entry;

    /// <summary>
    /// Gets the live entry for the key, or <see langword="null"/> when the key is absent or expired.
    /// </summary>
    public DatabaseEntry? Entry { get; }

    /// <summary>Gets the requested outcome.</summary>
    internal MutationOutcome Outcome { get; private set; }

    /// <summary>Gets the replacement entry when <see cref="Outcome"/> is <see cref="MutationOutcome.Replaced"/>.</summary>
    internal DatabaseEntry? Replacement { get; private set; }

    /// <summary>
    /// Declares that <see cref="Entry"/>'s value object was mutated in place.
    /// </summary>
    public void MarkMutated()
    {
        if (Entry is null)
        {
            throw new InvalidOperationException("Cannot mark a missing entry as mutated.");
        }

        Replacement = null;
        Outcome = MutationOutcome.InPlace;
    }

    /// <summary>
    /// Creates the key or replaces its entry.
    /// </summary>
    /// <param name="entry">Entry to store.</param>
    public void Replace(DatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Replacement = entry;
        Outcome = MutationOutcome.Replaced;
    }

    /// <summary>
    /// Removes the key.
    /// </summary>
    public void Remove()
    {
        Replacement = null;
        Outcome = MutationOutcome.Removed;
    }
}

/// <summary>
/// Synchronous read-modify-write delegate executed under a shard lock.
/// </summary>
/// <typeparam name="TState">Caller-supplied state, used to keep the delegate closure-free.</typeparam>
/// <typeparam name="TResult">Result returned to the caller.</typeparam>
/// <param name="state">Caller-supplied state.</param>
/// <param name="scope">Mutation handle for the key.</param>
/// <returns>The mutation result.</returns>
public delegate TResult StorageMutation<in TState, out TResult>(TState state, MutationScope scope);
