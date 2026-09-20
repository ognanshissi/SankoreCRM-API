namespace Sankore.Modules.Leads.Features.SlaMonitoring;

using Microsoft.Extensions.DependencyInjection;

public static class SlaMonitoringSetup
{
    /// <summary>
    /// Registers the <see cref="CheckSlaBreachesJob"/> as a transient service
    /// so Hangfire can resolve it from the DI container.
    /// </summary>
    public static IServiceCollection AddSlaMonitoring(this IServiceCollection services)
    {
        services.AddTransient<CheckSlaBreachesJob>();
        return services;
    }
}
