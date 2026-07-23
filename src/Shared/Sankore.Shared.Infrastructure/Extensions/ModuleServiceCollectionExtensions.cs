namespace Sankore.Shared.Infrastructure.Extensions;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Infrastructure.Outbox;

/// <summary>
/// One-liners every module calls from its own Add{ModuleName}Module() method
/// so that outbox wiring is consistent and nobody forgets a step.
/// </summary>
public static class ModuleServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox publisher (IEventPublisher) and the background
    /// processor for the given module DbContext type. Call once per module.
    ///
    /// IMPORTANT: registered as a KEYED service (key = TDbContext type name).
    /// A modular monolith shares ONE dependency injection container across
    /// all modules, so a plain `services.AddScoped&lt;IEventPublisher, ...&gt;()`
    /// call from every module would collide — whichever module registers
    /// last would silently "win" for every other module's handlers too.
    /// Keying by the module's own DbContext type keeps each module's
    /// publisher wired to its own outbox table, with no cross-module
    /// leakage possible. Handlers request their module's publisher via
    /// `[FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher`
    /// on the constructor parameter.
    /// </summary>
    public static IServiceCollection AddOutboxForModule<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        services.AddKeyedScoped<IEventPublisher, OutboxEventPublisher<TDbContext>>(typeof(TDbContext).Name);
        services.AddHostedService<OutboxProcessor<TDbContext>>();
        return services;
    }
}
