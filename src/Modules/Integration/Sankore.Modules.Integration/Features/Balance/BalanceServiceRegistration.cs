namespace Sankore.Modules.Integration.Features.Balance;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// What the balance area needs in the container. One call, wired from
/// <c>IntegrationModule.AddIntegrationModule</c>; the handler and the endpoint are found by the
/// assembly scans there.
/// </summary>
internal static class BalanceServiceRegistration
{
    internal static IServiceCollection AddBalanceServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // An explicit factory rather than AddScoped<LiveBalanceGate>(), for one parameter:
        // IDistributedCache is resolved with GetService and may legitimately be absent. A
        // constructor-injected IDistributedCache would make the gate — and through it every live
        // balance read — unresolvable on a deployment with no Redis, turning an optimisation into
        // a hard dependency. The Leads module resolves its cache the same way, and it is also what
        // keeps the gate constructible in a test with no cache at all.
        services.AddScoped(sp => new LiveBalanceGate(
            sp.GetService<IDistributedCache>(),
            sp.GetRequiredService<Infrastructure.Resilience.IntegrationResiliencePipelineProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LiveBalanceGate>>()));

        return services;
    }
}
