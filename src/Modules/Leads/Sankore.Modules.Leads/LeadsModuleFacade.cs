namespace Sankore.Modules.Leads;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;

/// <summary>
/// The only implementation of <see cref="ILeadsModule"/>: the seam through which
/// other modules read lead data without referencing this assembly.
///
/// Every method takes (or derives) an explicit tenant id and queries with
/// <c>IgnoreQueryFilters()</c> plus a manual <c>TenantId ==</c> predicate, because
/// callers may be MassTransit consumers or Hangfire jobs where the ambient
/// <see cref="Sankore.Shared.Kernel.ITenantContext"/> is not the tenant being read.
/// </summary>
public sealed class LeadsModuleFacade(LeadsDbContext db) : ILeadsModule
{
    public async Task<LeadSummary?> GetLeadAsync(Guid leadId, CancellationToken ct)
    {
        var lead = await db.Leads
            .Where(l => l.Id == leadId)
            .Select(l => new LeadSummary(
                l.Id,
                l.FullName,
                l.Status.ToString(),
                l.Source.ToString(),
                l.CurrentAssignedId))
            .FirstOrDefaultAsync(ct);

        return lead;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Privacy contract — read before adding a field to any summary below:
    /// only STRUCTURED, operator-chosen labels are projected (activity type, subject,
    /// outcome, reminder title, status). Free-text bodies (<c>LeadActivity.Notes</c>,
    /// <c>LeadReminder.Notes</c>) are deliberately excluded: an agent may paste a phone
    /// number or a document number into them, and this projection crosses a module
    /// boundary into a store (the client timeline) that has no reveal audit. Likewise
    /// excluded: <c>VisitLocation</c> (GPS), <c>VisitPhotoReference</c>,
    /// <c>CtiCallReference</c> and every field of the lead itself
    /// (phone, e-mail, national id, date of birth).
    /// </remarks>
    public async Task<IReadOnlyList<LeadHistoryItem>> GetLeadHistoryAsync(
        Guid tenantId, Guid leadId, CancellationToken ct)
    {
        var leadExists = await db.Leads
            .IgnoreQueryFilters()
            .AnyAsync(l => l.TenantId == tenantId && l.Id == leadId, ct);

        // An at-least-once consumer must not blow up on a lead that no longer exists.
        if (!leadExists) return [];

        var items = new List<LeadHistoryItem>();

        // ── Activities (calls, meetings, e-mails, notes…) and field visits ──────
        var activities = await db.LeadActivities
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == tenantId && a.LeadId == leadId)
            .Select(a => new
            {
                a.Id,
                a.Type,
                a.Subject,
                a.Outcome,
                a.PerformedAt,
                a.DurationMinutes,
                a.IsSystemGenerated,
            })
            .ToListAsync(ct);

        foreach (var a in activities)
        {
            var entryType = a.Type == ActivityType.Visit
                ? "LEAD_VISIT"
                : a.Type == ActivityType.Note
                    ? "LEAD_NOTE"
                    : $"LEAD_ACTIVITY_{a.Type.ToString().ToUpperInvariant()}";

            var summary = $"{Label(a.Type)} : {Trim(a.Subject)}";
            if (a.Outcome is not null)
                summary += $" — issue : {a.Outcome}";
            if (a.DurationMinutes is > 0)
                summary += $" ({a.DurationMinutes} min)";
            if (a.IsSystemGenerated)
                summary += " [automatique]";

            items.Add(new LeadHistoryItem(entryType, a.PerformedAt, summary, "LeadActivity", a.Id.ToString("D")));
        }

        // ── Follow-up reminders ────────────────────────────────────────────────
        var reminders = await db.LeadReminders
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId && r.LeadId == leadId)
            .Select(r => new { r.Id, r.Title, r.Status, r.DueAt, r.CreatedAt, r.ResolvedAt })
            .ToListAsync(ct);

        foreach (var r in reminders)
        {
            var summary = $"Relance « {Trim(r.Title)} » — {r.Status}";
            items.Add(new LeadHistoryItem(
                "LEAD_REMINDER",
                r.ResolvedAt ?? r.CreatedAt,
                summary,
                "LeadReminder",
                r.Id.ToString("D")));
        }

        return items.OrderBy(i => i.OccurredAt).ToList();
    }

    private static string Label(ActivityType type) => type switch
    {
        ActivityType.Call => "Appel",
        ActivityType.Meeting => "Rendez-vous",
        ActivityType.Email => "E-mail envoyé",
        ActivityType.Visit => "Visite terrain",
        ActivityType.Note => "Note",
        ActivityType.Sms => "SMS envoyé",
        ActivityType.WhatsApp => "Message WhatsApp",
        ActivityType.Task => "Tâche",
        _ => type.ToString(),
    };

    /// <summary>Keeps a summary well inside the 500-character timeline column.</summary>
    private static string Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(sans objet)";
        var trimmed = value.Trim();
        return trimmed.Length <= 180 ? trimmed : trimmed[..180] + "…";
    }
}
