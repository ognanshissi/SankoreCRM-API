namespace Sankore.Modules.Leads.Features.DispatchQueue;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Empties the dispatch queue: every lead a dispatch could not place
/// (<see cref="Lead.AwaitingDispatchSince"/>) is offered again, oldest first, under the tenant's
/// dispatching rule — the same <see cref="DispatchLeadCommand"/> as at capture, with no strategy
/// named so the rule picks it.
///
/// <para>
/// A sweep rather than a reaction to "an agent became available", because most of the ways an
/// agent becomes available are not events: a break or an absence that ends, working hours that
/// start, a task completed under the ceiling, an account activated, a role granted. A periodic
/// retry catches every one of them without each needing to know the queue exists.
/// </para>
///
/// <para>
/// Cheap when nothing can move: with no available agent in the whole tenant it stops after one
/// query. A retry that fails again leaves the lead queued and announces nothing — the handler only
/// alerts on the first entry. Runs under SYSTEM identity, per tenant.
/// </para>
/// </summary>
public sealed class DispatchWaitingLeadsJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Per run. A run every two minutes clears thousands an hour, and bounding it keeps one run from
    /// overlapping the next on a tenant that just imported a large file with nobody at work.
    /// </summary>
    internal const int BatchSize = 100;

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        await RunAsync(
            tenantId,
            sp.GetRequiredService<LeadsDbContext>(),
            sp.GetRequiredService<IAdministrationModule>(),
            sp.GetRequiredService<ISender>(),
            sp.GetRequiredService<ILogger<DispatchWaitingLeadsJob>>(),
            CancellationToken.None);
    }

    /// <summary>The run itself, apart from the Hangfire scope plumbing, so a test can drive it.</summary>
    internal static async Task<int> RunAsync(
        Guid tenantId,
        LeadsDbContext db,
        IAdministrationModule administration,
        ISender sender,
        ILogger logger,
        CancellationToken ct)
    {
        var waiting = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && l.AwaitingDispatchSince != null
                     && l.CurrentAssignmentId == null
                     && l.Status != LeadStatus.Converted
                     && l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Disqualified
                     && l.Status != LeadStatus.Archived)
            .OrderBy(l => l.AwaitingDispatchSince)
            .Select(l => l.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (waiting.Count == 0) return 0;

        // Nobody available anywhere: every dispatch below would fail the same way.
        var anyone = await administration.GetAvailableAgentsAsync(tenantId, agencyId: null, ct);
        if (anyone.Count == 0) return 0;

        var placed = 0;
        foreach (var leadId in waiting)
        {
            try
            {
                var result = await sender.Send(new DispatchLeadCommand(leadId, tenantId, Strategy: null), ct);
                if (result.IsSuccess) placed++;
            }
            catch (Exception ex)
            {
                // One lead that throws must not keep the rest of the queue waiting.
                logger.LogWarning(ex, "Queued lead {LeadId} could not be retried", leadId);
            }
        }

        logger.LogInformation(
            "Dispatch queue of tenant {TenantId}: {Placed} of {Waiting} waiting lead(s) placed",
            tenantId, placed, waiting.Count);
        return placed;
    }
}
