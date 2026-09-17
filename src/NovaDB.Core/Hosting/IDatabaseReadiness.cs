namespace NovaDB.Core.Hosting;

/// <summary>
/// Reports whether the database has finished startup recovery and may serve clients.
/// </summary>
public interface IDatabaseReadiness
{
    /// <summary>Gets a value indicating whether recovery completed successfully.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Waits until recovery completes or <paramref name="cancellationToken"/> fires.
    /// </summary>
    Task WaitAsync(CancellationToken cancellationToken = default);
}
