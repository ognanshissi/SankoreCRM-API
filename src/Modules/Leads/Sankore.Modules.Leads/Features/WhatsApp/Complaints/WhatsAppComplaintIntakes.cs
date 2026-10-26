namespace Sankore.Modules.Leads.Features.WhatsApp.Complaints;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The complaint intakes follow their conversation when it is erased or purged — called next to
/// the bot turns' anonymisation by the three paths that do it. The caller saves.
/// </summary>
internal static class WhatsAppComplaintIntakes
{
    public static async Task AnonymizeAsync(
        LeadsDbContext db, Guid tenantId, IReadOnlyCollection<Guid> conversationIds, DateTimeOffset now, CancellationToken ct)
    {
        if (conversationIds.Count == 0) return;

        var intakes = await db.WhatsAppComplaintIntakes
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(i => i.TenantId == tenantId && conversationIds.Contains(i.ConversationId))
            .ToListAsync(ct);

        foreach (var intake in intakes) intake.Anonymize(now);
    }
}
