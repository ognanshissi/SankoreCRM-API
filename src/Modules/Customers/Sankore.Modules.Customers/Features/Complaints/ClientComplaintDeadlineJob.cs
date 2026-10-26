namespace Sankore.Modules.Customers.Features.Complaints;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// Chases the client complaints whose acknowledgement or answer is late (2026-10-26): one alert
/// per complaint and per deadline, to the agency manager — the assignee already has a task, and a
/// deadline they let pass is exactly what their manager must hear about. No manager: the assignee.
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class ClientComplaintDeadlineJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ClientComplaintDeadlineJob> logger)
{
    private const int PageSize = 200;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity BEFORE the DI scope: ITenantContext and ICurrentUser are built from it.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var reported = await ReportAsync(
            sp.GetRequiredService<CustomersDbContext>(),
            sp.GetRequiredKeyedService<IEventPublisher>(nameof(CustomersDbContext)),
            sp.GetService<IAdministrationModule>(),
            tenantId,
            sp.GetRequiredService<TimeProvider>().GetUtcNow(),
            ct);

        if (reported > 0)
            logger.LogWarning("Client complaints of tenant {TenantId}: {Count} late deadline(s) reported.", tenantId, reported);
    }

    internal static async Task<int> ReportAsync(
        CustomersDbContext db, IEventPublisher publisher, IAdministrationModule? administration,
        Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        // Open, and one of the deadlines passed — the aggregate decides which is still unreported.
        var late = await db.ClientComplaints
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId
                     && (c.Status == ClientComplaintStatus.Received || c.Status == ClientComplaintStatus.Acknowledged)
                     && ((c.ResolutionDueAt < now && c.ResolutionOverdueNotifiedAt == null)
                      || (c.AcknowledgedAt == null && c.AcknowledgementDueAt < now && c.AcknowledgementOverdueNotifiedAt == null)))
            .OrderBy(c => c.AcknowledgementDueAt)
            .Take(PageSize)
            .ToListAsync(ct);

        var reported = 0;

        foreach (var complaint in late)
        {
            if (complaint.OverdueToReport(now) is not { } deadline) continue;

            var manager = administration is null
                ? null
                : await administration.ResolveAgencyRecipientAsync(tenantId, complaint.AgencyId, preferredUserId: null, ct);

            // Marked even with nobody to tell: the register shows it late, and a daily retry that
            // can never succeed would only fill the log.
            complaint.MarkOverdueReported(deadline, now);

            if ((manager ?? complaint.AssignedTo) is not { } recipient) continue;

            await publisher.PublishAsync(new ClientComplaintActionRequiredEvent(
                tenantId, complaint.ClientId, complaint.Id, complaint.Reference, complaint.Subject, recipient,
                deadline == ClientComplaintDeadline.Resolution
                    ? ClientComplaintActionReasons.ResolutionOverdue
                    : ClientComplaintActionReasons.AcknowledgementOverdue,
                deadline == ClientComplaintDeadline.Resolution ? complaint.ResolutionDueAt : complaint.AcknowledgementDueAt), ct);
            reported++;
        }

        await db.SaveChangesAsync(ct);
        return reported;
    }
}
