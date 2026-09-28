namespace Sankore.Modules.Customers.Features.Duplicates;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Customers.Features.Duplicates.BackfillPhoneticKeys;
using Sankore.Modules.Customers.Features.Duplicates.DetectDuplicates;
using Sankore.Modules.Customers.Features.Duplicates.Merge;

/// <summary>
/// DI registrations owned by the deduplication zone. Called by <c>CustomersModule</c>.
/// </summary>
internal static class DuplicatesServiceRegistration
{
    internal static IServiceCollection AddDuplicatesServices(this IServiceCollection services)
    {
        // Merge execution: scoped, it works on the request's DbContext and its outbox.
        services.AddScoped<IClientMergeExecutor, ClientMergeExecutor>();

        // Hangfire job types. Transient, like every other job in the solution: Hangfire resolves one
        // instance per execution and each job opens its own service scope internally.
        // The cron schedules are declared in Program.cs, not here:
        //   * "customers-detect-duplicates-orchestrator" → 0 2 * * *  (global, fans out per tenant)
        // BackfillPhoneticKeysJob and DetectDuplicatesJob are enqueued on demand, never scheduled.
        services.AddTransient<BackfillPhoneticKeysJob>();
        services.AddTransient<DetectDuplicatesJob>();
        services.AddTransient<DetectDuplicatesOrchestratorJob>();

        return services;
    }
}
