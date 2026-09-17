using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NovaDB.Journal;

/// <summary>
/// DI registration for the durable command journal.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICommandJournal"/> and starts it as a hosted service.
    /// </summary>
    public static IServiceCollection AddNovaDbJournal(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<FileCommandJournal>();
        services.TryAddSingleton<ICommandJournal>(sp => sp.GetRequiredService<FileCommandJournal>());
        services.TryAddSingleton<ITimeTravelInspector, TimeTravelInspector>();
        services.AddHostedService(sp => sp.GetRequiredService<FileCommandJournal>());
        return services;
    }
}
