using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Fans mutating commands out to multiple sinks (AOF + journal).
/// </summary>
public sealed class CompositeCommandMutationSink : ICommandMutationSink
{
    private readonly ICommandMutationSink[] _sinks;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeCommandMutationSink"/> class.
    /// </summary>
    public CompositeCommandMutationSink(IEnumerable<ICommandMutationSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.Where(static s => s is not null).ToArray();
        if (_sinks.Length == 0)
        {
            throw new ArgumentException("At least one mutation sink is required.", nameof(sinks));
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken)
    {
        for (var i = 0; i < _sinks.Length; i++)
        {
            await _sinks[i].OnMutatingCommandAsync(commandArray, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandAsync(
        RespValue commandArray,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < _sinks.Length; i++)
        {
            await _sinks[i].OnMutatingCommandAsync(commandArray, metadata, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < _sinks.Length; i++)
        {
            await _sinks[i].OnMutatingCommandBatchAsync(commandArrays, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < _sinks.Length; i++)
        {
            await _sinks[i].OnMutatingCommandBatchAsync(commandArrays, metadata, cancellationToken).ConfigureAwait(false);
        }
    }
}
