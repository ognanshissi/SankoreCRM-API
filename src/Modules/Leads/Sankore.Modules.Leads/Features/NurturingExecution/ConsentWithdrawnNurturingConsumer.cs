namespace Sankore.Modules.Leads.Features.NurturingExecution;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Integration event published by WithdrawConsentHandler when marketing consent is withdrawn.
/// </summary>
public sealed record MarketingConsentWithdrawnEvent(Guid TenantId, Guid LeadId);

/// <summary>
/// MassTransit consumer that immediately cancels all active nurturing enrollments
/// for a lead when their Marketing consent is withdrawn (US-M13-151).
/// Event-driven — no polling required.
/// </summary>
public sealed class ConsentWithdrawnNurturingConsumer(
    LeadsDbContext db,
    ILogger<ConsentWithdrawnNurturingConsumer> logger)
    : IConsumer<MarketingConsentWithdrawnEvent>
{
    public async Task Consume(ConsumeContext<MarketingConsentWithdrawnEvent> context)
    {
        var evt = context.Message;

        var activeEnrollments = await db.NurturingEnrollments
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == evt.TenantId
                     && e.LeadId == evt.LeadId
                     && e.Status == NurturingEnrollmentStatus.Active)
            .ToListAsync(context.CancellationToken);

        if (activeEnrollments.Count == 0) return;

        var clock = TimeProvider.System;
        foreach (var enrollment in activeEnrollments)
            enrollment.Cancel("MARKETING_CONSENT_WITHDRAWN", clock);

        await db.SaveChangesAsync(context.CancellationToken);

        logger.LogInformation(
            "Cancelled {Count} nurturing enrollment(s) for lead {LeadId} (consent withdrawn)",
            activeEnrollments.Count, evt.LeadId);
    }
}
