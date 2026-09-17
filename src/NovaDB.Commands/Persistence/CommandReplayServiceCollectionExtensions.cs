using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaDB.Persistence.Aof;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Dependency injection extensions that connect AOF recovery to the command layer.
/// </summary>
public static class CommandReplayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the replayer that applies AOF records through the live command handlers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// The persistence layer cannot reference the command layer, so recovery has no replayer until
    /// this is called. Call it after <c>AddNovaDbPersistence</c> and <c>AddNovaDbCommands</c>.
    /// </remarks>
    public static IServiceCollection AddNovaDbCommandReplay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAofCommandReplayer, DispatchingAofCommandReplayer>();
        return services;
    }
}
