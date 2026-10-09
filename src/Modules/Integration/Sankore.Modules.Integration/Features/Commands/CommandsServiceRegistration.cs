namespace Sankore.Modules.Integration.Features.Commands;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sankore.Modules.Integration.Features.Commands.Execute;
using Sankore.Modules.Integration.Features.References.GetReference;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// Everything INT-05 and INT-07 put in the container. Called once from
/// <c>AddIntegrationModule</c>, so the module file keeps one line per area instead of growing a
/// registration per slice.
/// </summary>
internal static class CommandsServiceRegistration
{
    internal static IServiceCollection AddCommandsServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, like the DbContext they read: both hold a unit of work per request or per job.
        services.AddScoped<CommandPayloadProtector>();
        services.AddScoped<CbsCustomerPayloadSource>();
        services.AddScoped<ReferenceLookup>();

        // The public contract. Scoped because it reads through the module's DbContext, and the
        // Request… methods must enlist in the CALLER's unit of work — a singleton holding its own
        // context would defeat the whole design (see IntegrationModuleFacade).
        services.AddScoped<IIntegrationModule, IntegrationModuleFacade>();

        // ICommandRetryPolicy is NOT registered here. Its implementation belongs to the
        // dispatcher (INT-06), which owns the schedule and binds Integration:Retry:* with
        // ValidateOnStart — see AddDispatchServices. A second registration of the same interface
        // from here, even a TryAdd, would make the platform's attempt budget depend on the order
        // the module happens to call its areas in.

        // TryAdd so the batch socle (INT-24), registered before this call, wins. The default
        // merely states that this deployment has no outbound file writer.
        services.TryAddScoped<IBatchFileEnlister, UnavailableBatchFileEnlister>();

        return services;
    }
}
