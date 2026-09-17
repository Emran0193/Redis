using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaDB.Commands.Handlers;
using NovaDB.Commands.Persistence;
using NovaDB.Commands.PubSub;
using NovaDB.Monitoring;
using NovaDB.Networking;

namespace NovaDB.Commands;

/// <summary>
/// Dependency injection registration for NovaDB command handlers.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers command dispatch, processing, and all built-in command handlers.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNovaDbCommands(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INovaDbMetrics>(_ => NullNovaDbMetrics.Instance);
        services.TryAddSingleton<AuthRateLimiter>();
        services.TryAddSingleton<AuthPasswordVerifier>();

        services.AddSingleton<ICommandMutationSink>(sp =>
        {
            var persistenceSink = sp.GetService<NovaDB.Persistence.ICommandMutationSink>();
            return persistenceSink is null
                ? NullCommandMutationSink.Instance
                : new PersistenceMutationSinkAdapter(persistenceSink);
        });
        services.AddSingleton<PubSubSubscriberCache>();
        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<ICommandExecutor>(static sp => sp.GetRequiredService<CommandDispatcher>());
        services.AddSingleton<ICommandProcessor, CommandProcessor>();

        RegisterHandler<PingCommandHandler>(services);
        RegisterHandler<EchoCommandHandler>(services);
        RegisterHandler<QuitCommandHandler>(services);
        RegisterHandler<AuthCommandHandler>(services);
        RegisterHandler<AclCommandHandler>(services);
        RegisterHandler<HelloCommandHandler>(services);
        RegisterHandler<SelectCommandHandler>(services);
        RegisterHandler<ConfigCommandHandler>(services);
        RegisterHandler<ClientCommandHandler>(services);
        RegisterHandler<CommandCommandHandler>(services);
        RegisterHandler<InfoCommandHandler>(services);
        RegisterHandler<MemoryCommandHandler>(services);

        RegisterHandler<SetCommandHandler>(services);
        RegisterHandler<SetexCommandHandler>(services);
        RegisterHandler<SetnxCommandHandler>(services);
        RegisterHandler<PsetexCommandHandler>(services);
        RegisterHandler<GetCommandHandler>(services);
        RegisterHandler<GetexCommandHandler>(services);
        RegisterHandler<GetdelCommandHandler>(services);
        RegisterHandler<MgetCommandHandler>(services);
        RegisterHandler<MsetCommandHandler>(services);
        RegisterHandler<MsetnxCommandHandler>(services);
        RegisterHandler<DelCommandHandler>(services);
        RegisterHandler<UnlinkCommandHandler>(services);
        RegisterHandler<ExistsCommandHandler>(services);
        RegisterHandler<TouchCommandHandler>(services);
        RegisterHandler<StrlenCommandHandler>(services);
        RegisterHandler<IncrCommandHandler>(services);
        RegisterHandler<DecrCommandHandler>(services);
        RegisterHandler<IncrbyCommandHandler>(services);
        RegisterHandler<DecrbyCommandHandler>(services);
        RegisterHandler<IncrbyfloatCommandHandler>(services);
        RegisterHandler<AppendCommandHandler>(services);

        RegisterHandler<ExpireCommandHandler>(services);
        RegisterHandler<PexpireatCommandHandler>(services);
        RegisterHandler<TtlCommandHandler>(services);
        RegisterHandler<PttlCommandHandler>(services);
        RegisterHandler<PersistCommandHandler>(services);

        RegisterHandler<HsetCommandHandler>(services);
        RegisterHandler<HgetCommandHandler>(services);
        RegisterHandler<HmgetCommandHandler>(services);
        RegisterHandler<HdelCommandHandler>(services);
        RegisterHandler<HgetallCommandHandler>(services);
        RegisterHandler<HincrbyCommandHandler>(services);
        RegisterHandler<HexistsCommandHandler>(services);
        RegisterHandler<HlenCommandHandler>(services);
        RegisterHandler<HkeysCommandHandler>(services);
        RegisterHandler<HvalsCommandHandler>(services);

        RegisterHandler<LpushCommandHandler>(services);
        RegisterHandler<RpushCommandHandler>(services);
        RegisterHandler<LpopCommandHandler>(services);
        RegisterHandler<RpopCommandHandler>(services);
        RegisterHandler<LrangeCommandHandler>(services);

        RegisterHandler<SaddCommandHandler>(services);
        RegisterHandler<SremCommandHandler>(services);
        RegisterHandler<SmembersCommandHandler>(services);

        RegisterHandler<ZaddCommandHandler>(services);
        RegisterHandler<ZrangeCommandHandler>(services);
        RegisterHandler<ZscoreCommandHandler>(services);

        RegisterHandler<DbsizeCommandHandler>(services);
        RegisterHandler<FlushdbCommandHandler>(services);
        RegisterHandler<FlushallCommandHandler>(services);
        RegisterHandler<KeysCommandHandler>(services);
        RegisterHandler<ScanCommandHandler>(services);
        RegisterHandler<BgRewriteAofCommandHandler>(services);

        RegisterHandler<MultiCommandHandler>(services);
        RegisterHandler<ExecCommandHandler>(services);
        RegisterHandler<DiscardCommandHandler>(services);
        RegisterHandler<WatchCommandHandler>(services);
        RegisterHandler<UnwatchCommandHandler>(services);

        RegisterHandler<SubscribeCommandHandler>(services);
        RegisterHandler<UnsubscribeCommandHandler>(services);
        RegisterHandler<PublishCommandHandler>(services);

        return services;
    }

    private static void RegisterHandler<THandler>(IServiceCollection services)
        where THandler : class, ICommandHandler
    {
        services.AddSingleton<ICommandHandler, THandler>();
    }
}
