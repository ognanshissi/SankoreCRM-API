namespace Sankore.Modules.Integration.Features.Insurance.Complaints;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Chases the two deadlines of the complaints mechanism before they are missed, and reports the
/// ones already breached.
///
/// <para>
/// <b>Why a job at all.</b> A complaints mechanism the 2024 règlement would accept is one that
/// ANSWERS, and nothing in a register makes an unanswered complaint visible: the agent who
/// recorded it has moved on, and a screen only shows what somebody opens. The deadline is the
/// whole obligation, so something has to notice it passing.
/// </para>
///
/// <para>
/// <b>It warns BEFORE the deadline, not only after.</b> A notification sent the day a deadline is
/// breached is a record of failure; one sent while there is still time is the mechanism working.
/// So the sweep looks for complaints whose deadline falls within <see cref="WarningDays"/> — and
/// also reports the already-breached, because those are what a supervisor asks about and nobody
/// else would surface them.
/// </para>
///
/// <para>
/// <b>Addressed to whoever RECORDED it</b>, which is the only person this module knows. An IMF
/// with a dedicated complaints unit would want the unit's mailbox instead; that is a tenant
/// setting this module does not have, and guessing a recipient is worse than telling the person
/// who took the complaint down.
/// </para>
/// </summary>
// Public, not internal: Program.cs registers the recurring job by type.
public sealed class ComplaintDeadlineJob(
    IServiceScopeFactory scopeFactory,
    ILogger<ComplaintDeadlineJob> logger)
{
    internal const string DeadlineTemplate = "integration.complaints.deadline_approaching";

    /// <summary>
    /// How far ahead a deadline is chased. Three days: long enough that an answer can still be
    /// written, short enough that the warning is about THIS complaint rather than a standing
    /// backlog nobody reads.
    /// </summary>
    internal const int WarningDays = 3;

    /// <summary>Complaints per run. A ceiling, so one tenant's backlog cannot hold a connection.</summary>
    private const int PageSize = 200;

    public async Task RunAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) return;

        // SYSTEM identity, established BEFORE the DI scope: ITenantContext and ICurrentUser are
        // built from it and a Hangfire job has no HTTP context.
        using var bg = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<IntegrationDbContext>();
        var administration = sp.GetRequiredService<IAdministrationModule>();
        var clock = sp.GetRequiredService<TimeProvider>();

        var now = clock.GetUtcNow();
        var horizon = now.AddDays(WarningDays);

        // IgnoreQueryFilters with an explicit tenant predicate: a Hangfire job runs outside any
        // HTTP context, so the ambient tenant cannot be trusted to be the one being processed.
        //
        // The OPEN ones whose answer is due within the horizon — which includes everything
        // already breached, since a past date is also before the horizon. The acknowledgement
        // deadline is the earlier of the two and is always inside it when the answer's is, so one
        // predicate serves both and the decision of WHICH is late is left to the aggregate.
        var due = await db.InsuranceComplaints
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && (c.Status == ComplaintStatus.Received
                      || c.Status == ComplaintStatus.Acknowledged
                      || c.Status == ComplaintStatus.Escalated)
                     && (c.ResolutionDueAt <= horizon || c.AcknowledgementDueAt <= horizon))
            .OrderBy(c => c.ResolutionDueAt)
            .Take(PageSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        var warned = 0;
        var breached = 0;

        foreach (var complaint in due)
        {
            var acknowledgementLate = complaint.AcknowledgementOverdueAt(now);
            var resolutionLate = complaint.ResolutionOverdueAt(now);

            if (acknowledgementLate || resolutionLate) breached++;

            if (complaint.ReceivedBy == Guid.Empty)
            {
                // Recorded by SYSTEM, through a digital channel with no agent behind it. Nobody to
                // address, and a notification to the empty user is a failed send that reads as a
                // bug. It still counts as breached — the report is what surfaces it.
                continue;
            }

            // Keyed on WHAT the notification is about: the complaint and WHICH deadline. So a
            // daily sweep over the same complaint re-sends nothing, and the answer's deadline
            // arriving after the acknowledgement's is a second, distinct warning.
            var stage = resolutionLate || complaint.AcknowledgedAt is not null
                ? "resolution"
                : "acknowledgement";

            var sent = await administration.NotifyUserAsync(
                new NotifyUserRequest(
                    TenantId: tenantId,
                    UserId: complaint.ReceivedBy,
                    TemplateKey: DeadlineTemplate,
                    IdempotencyKey: $"{DeadlineTemplate}-{complaint.Id}-{stage}",
                    TemplateData: new Dictionary<string, object>
                    {
                        // The reference, the subject and the dates. Deliberately NOT the body: a
                        // grievance is free text about a named person, and a rendered body travels
                        // to a mail provider and sits in its delivery log.
                        ["complaint_reference"] = complaint.Reference,
                        ["complaint_subject"] = complaint.Subject,
                        ["due_at"] = (stage == "acknowledgement"
                            ? complaint.AcknowledgementDueAt
                            : complaint.ResolutionDueAt).ToString("yyyy-MM-dd"),
                        ["stage"] = stage,
                        ["is_late"] = acknowledgementLate || resolutionLate,
                    }),
                ct);

            if (sent) warned++;
        }

        // Warning and not Information when something is already late: a breached deadline of a
        // regulatory mechanism belongs in whatever an operator actually reads.
        if (breached > 0)
        {
            logger.LogWarning(
                "Complaints for tenant {TenantId}: {Breached} deadline(s) already BREACHED out of "
                + "{Due} approaching; {Warned} agent(s) notified",
                tenantId, breached, due.Count, warned);
        }
        else
        {
            logger.LogInformation(
                "Complaints for tenant {TenantId}: {Due} deadline(s) approaching, "
                + "{Warned} agent(s) notified", tenantId, due.Count, warned);
        }
    }
}
