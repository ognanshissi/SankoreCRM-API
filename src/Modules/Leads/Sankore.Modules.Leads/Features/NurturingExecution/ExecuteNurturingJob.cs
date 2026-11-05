namespace Sankore.Modules.Leads.Features.NurturingExecution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Retention;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

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
        var customers = sp.GetService<ICustomersModule>();
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
            .Select(l => new { l.Id, l.Status, l.PreferredLanguage, l.Email, l.FullName, l.FirstName, ClientId = l.ConvertedToCustomerId ?? l.CustomerId })
            .ToDictionaryAsync(l => l.Id);

        // Read at each run: a consent can also expire, and that announces nothing.
        var consenting = (await db.LeadConsents
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId
                     && leadIds.Contains(c.LeadId)
                     && c.Type == ConsentType.Marketing
                     && c.Status == ConsentStatus.Active)
            .Select(c => c.LeadId)
            .ToListAsync()).ToHashSet();

        var templates = new Dictionary<(string Key, string Locale), bool>();

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

            // The lead left nurturing (reopened, converted, closed, merged, erased): nothing changed
            // the enrollment, and a commercial e-mail must not reach them for all that.
            if (!leads.TryGetValue(enrollment.LeadId, out var lead) || lead.Status != LeadStatus.Nurturing)
            {
                enrollment.Cancel("LEAD_LEFT_NURTURING", clock);
                skipped++;
                continue;
            }

            if (!consenting.Contains(enrollment.LeadId))
            {
                enrollment.Cancel("MARKETING_CONSENT_WITHDRAWN", clock);
                skipped++;
                continue;
            }

            if (await ProcessingRestriction.IsRestrictedAsync(customers, tenantId, lead.ClientId, CancellationToken.None))
            {
                enrollment.Cancel(ProcessingRestriction.Error, clock);
                skipped++;
                continue;
            }

            // Resolve lead email — skip if no email available
            if (lead.Email is null)
            {
                skipped++;
                continue;
            }

            // The lead's language, else French. With no template at all the step waits: the renderer
            // would send the message's raw JSON to the prospect.
            var locale = LanguageCode.Normalize(lead.PreferredLanguage);
            if (!await HasTemplateAsync(step.EmailTemplateKey, locale))
                locale = LanguageCode.Default;
            if (!await HasTemplateAsync(step.EmailTemplateKey, locale))
            {
                logger.LogWarning(
                    "Nurturing step {StepIndex} of sequence {SequenceId} waits: no active template {TemplateKey}",
                    nextStepIndex, sequence.Id, step.EmailTemplateKey);
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
                    Locale:         locale,
                    TemplateData: new Dictionary<string, object>
                    {
                        ["leadId"]   = enrollment.LeadId.ToString(),
                        ["stepOrder"] = step.Order,
                        ["lead_name"] = lead.FullName ?? string.Empty,
                        ["first_name"] = lead.FirstName ?? lead.FullName ?? string.Empty,
                    },
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

        async Task<bool> HasTemplateAsync(string key, string locale)
        {
            if (templates.TryGetValue((key, locale), out var known)) return known;
            return templates[(key, locale)] = await notif.HasActiveEmailTemplateAsync(tenantId, key, locale);
        }
    }
}
