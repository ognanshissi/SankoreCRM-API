namespace Sankore.Modules.Customers.Features.Lifecycle;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI registrations owned by the <c>Lifecycle</c> zone. Called once by
/// <c>CustomersModule.AddCustomersModule</c>; no other zone touches it.
/// </summary>
internal static class LifecycleServiceRegistration
{
    internal static IServiceCollection AddLifecycleServices(this IServiceCollection s)
    {
        // Extension point for M03 (Savings) / M04 (Credit): swap this line for a
        // probe that fans out to their PublicApi and the archive blocking rule
        // (CLIENT_HAS_ACTIVE_COMMITMENTS) starts firing with no change to
        // ArchiveClientHandler. See IOutstandingBalanceProbe.
        s.AddScoped<IOutstandingBalanceProbe, NoOutstandingBalanceProbe>();

        return s;
    }
}
