namespace Sankore.Modules.Leads.Features.NurturingExecution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire recurring job — runs per-tenant on a cron schedule (US-M13-151).
/// For each active enrollment whose next step is due, sends the email via M08
/// and advances the enrollment. Payload contains only opaque lead IDs — never PII.
/// Runs under SYSTEM identity.
/// </summary>
public sealed class ExecuteNurturingJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Hangfire entry point. Only the tenant ID is serialized — opaque identifier only.</summary>
    public async Task ExecuteAsync(Guid tenantId)
    {
        var systemUserId = Guid.Empty;
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, systemUserId, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sp     = scope.ServiceProvider;
        var db     = sp.GetRequiredService<LeadsDbContext>();
        var notif  = sp.GetRequiredService<INotificationsModule>();
        var clock  = sp.GetRequiredService<TimeProvider>();
        var logger = sp.GetRequiredService<ILogger<ExecuteNurturingJob>>();

        var now = clock.GetUtcNow();

        // ── Find enrollments with a due step ────────────────────────────
        var dueEnrollments = await db.NurturingEnrollments
            .AsTracking()
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && e.Status == NurturingEnrollmentStatus.Active
                     && e.NextStepDueAt <= now)
            .ToListAsync();

        if (dueEnrollments.Count == 0) return;

        // Pre-load sequences + steps
        var sequenceIds = dueEnrollments.Select(e => e.SequenceId).Distinct().ToList();
        var sequences = await db.NurturingSequences
            .IgnoreQueryFilters()
            .Include(s => s.Steps)
            .Where(s => sequenceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id);

        // Pre-load leads for email context (only opaque IDs + preferred language)
        var leadIds = dueEnrollments.Select(e => e.LeadId).Distinct().ToList();
        var leads = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => leadIds.Contains(l.Id))
            .Select(l => new { l.Id, l.PreferredLanguage, l.Email, l.FullName })
            .ToDictionaryAsync(l => l.Id);

        int sent = 0, completed = 0, skipped = 0;

        foreach (var enrollment in dueEnrollments)
        {
            if (!sequences.TryGetValue(enrollment.SequenceId, out var sequence)
                || !sequence.IsActive)
            {
                enrollment.Cancel("SEQUENCE_DEACTIVATED", clock);
                skipped++;
                continue;
            }

            var steps = sequence.Steps.OrderBy(s => s.Order).ToList();
            var nextStepIndex = enrollment.LastCompletedStepIndex + 1;

            if (nextStepIndex >= steps.Count)
            {
                enrollment.Complete(clock);
                completed++;
                continue;
            }

            var step = steps[nextStepIndex];

            // Resolve lead email — skip if no email available
            if (!leads.TryGetValue(enrollment.LeadId, out var lead) || lead.Email is null)
            {
                skipped++;
                continue;
            }

            // ── Send via M08 ────────────────────────────────────────────
            try
            {
                await notif.QueueEmailAsync(new QueueEmailRequest(
                    TemplateKey:    step.EmailTemplateKey,
                    RecipientEmail: lead.Email,
                    RecipientName:  lead.FullName,
                    Module:         "Leads",
                    Locale:         lead.PreferredLanguage ?? "fr",
                    TemplateData: new Dictionary<string, object>
                    {
                        ["leadId"]   = enrollment.LeadId.ToString(),
                        ["stepOrder"] = step.Order,
                    },
                    // Opaque payload: only IDs, never PII in job arguments
                    IdempotencyKey: $"nurture-{enrollment.Id}-step-{nextStepIndex}",
                    TenantId:       tenantId));

                // Advance enrollment
                var isLast = nextStepIndex + 1 >= steps.Count;
                if (isLast)
                {
                    enrollment.Complete(clock);
                    completed++;
                }
                else
                {
                    var nextStep = steps[nextStepIndex + 1];
                    enrollment.AdvanceStep(nextStep.DelayFromPrevious, clock);
                }

                sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to send nurturing step {StepIndex} for enrollment {EnrollmentId}",
                    nextStepIndex, enrollment.Id);
            }
        }

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Nurturing execution for tenant {TenantId}: {Sent} sent, {Completed} completed, {Skipped} skipped",
            tenantId, sent, completed, skipped);
    }
}
