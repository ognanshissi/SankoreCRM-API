namespace Sankore.Modules.Customers.Features.Compliance.Erasure;

using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>Enqueues <see cref="ProcessRestrictedErasuresJob"/> for every active tenant, daily.</summary>
public sealed class ProcessRestrictedErasuresOrchestratorJob(IServiceScopeFactory scopeFactory, ITenantStore tenantStore)
{
    public const string RecurringJobId = "customers-restricted-erasures";

    public async Task ExecuteAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var hangfire = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();

        foreach (var tenant in await tenantStore.GetAllActiveAsync(CancellationToken.None))
            if (tenant.Id != Guid.Empty)
                hangfire.Enqueue<ProcessRestrictedErasuresJob>(job => job.ExecuteAsync(tenant.Id));
    }
}

/// <summary>
/// The approved erasures that had to wait (data-purge lot 2): each day, every restricted request
/// is evaluated again, and erased on the day its client's legal retention is over. The decision
/// was taken by two people already; the date is the only thing left to come.
/// </summary>
public sealed class ProcessRestrictedErasuresJob(IServiceScopeFactory scopeFactory, ILogger<ProcessRestrictedErasuresJob> logger)
{
    public async Task ExecuteAsync(Guid tenantId)
    {
        // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");

        List<Guid> ids;
        using (var scope = scopeFactory.CreateScope())
        {
            ids = await scope.ServiceProvider.GetRequiredService<CustomersDbContext>().ErasureRequests
                .IgnoreQueryFilters()
                .Where(r => r.TenantId == tenantId && r.Status == ErasureRequestStatus.Restricted)
                .Select(r => r.Id)
                .ToListAsync();
        }

        var erased = 0;
        foreach (var id in ids)
        {
            // One scope per request: one that fails must not hold back the others.
            using var scope = scopeFactory.CreateScope();
            var sp = scope.ServiceProvider;
            try
            {
                var db = sp.GetRequiredService<CustomersDbContext>();
                var request = await db.ErasureRequests.IgnoreQueryFilters().AsTracking().FirstAsync(r => r.Id == id);
                var verdict = await sp.GetRequiredService<ErasureEligibility>()
                    .EvaluateAsync(tenantId, request.SubjectType, request.SubjectId, CancellationToken.None);
                var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();

                if (verdict.Eligible)
                {
                    await sp.GetRequiredService<ErasureExecutor>().ExecuteAsync(request, verdict, Guid.Empty, CancellationToken.None);
                    request.Complete(Guid.Empty, now, null);
                    erased++;
                }
                else if (verdict.AlreadyErased || !verdict.Found)
                {
                    request.Complete(Guid.Empty, now, null);
                }
                else
                {
                    request.StillBlocked(verdict.Blocker!, verdict.EligibleFrom);
                }

                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Restricted erasure {RequestId} of tenant {TenantId} could not be processed.", id, tenantId);
            }
        }

        if (erased > 0)
            logger.LogWarning("{Count} restricted erasure(s) of tenant {TenantId} ran today.", erased, tenantId);
    }
}
