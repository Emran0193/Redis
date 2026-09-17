namespace NovaDB.Persistence.Recovery;

/// <summary>
/// Signals when startup recovery has completed and the database is ready to serve traffic.
/// </summary>
public sealed class DatabaseRecoveryGate : NovaDB.Core.Hosting.IDatabaseReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Gets a task that completes when recovery has finished successfully.
    /// </summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// Gets a value indicating whether recovery has completed.
    /// </summary>
    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Marks recovery as complete so dependent services may start.
    /// </summary>
    public void MarkReady() => _ready.TrySetResult();

    /// <summary>
    /// Marks recovery as failed so dependent services can observe the fault.
    /// </summary>
    /// <param name="exception">Failure exception.</param>
    public void MarkFailed(Exception exception) => _ready.TrySetException(exception);

    /// <inheritdoc />
    public Task WaitAsync(CancellationToken cancellationToken = default)
        => _ready.Task.WaitAsync(cancellationToken);
}
