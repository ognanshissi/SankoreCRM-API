namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The open handover of a conversation (WA-BE-22) — at most one, found the same way by every act
/// that ends or moves it. Never saves: each caller commits it with the change that caused it.
/// </summary>
internal static class WhatsAppHandovers
{
    public static async Task<WhatsAppHandover?> FindOpenAsync(LeadsDbContext db, Guid conversationId, CancellationToken ct) =>
        db.WhatsAppHandovers.Local.FirstOrDefault(h => h.ConversationId == conversationId && h.EndedAt == null)
        ?? await db.WhatsAppHandovers
            .AsTracking()
            .Where(h => h.ConversationId == conversationId && h.EndedAt == null)
            .OrderByDescending(h => h.EscalatedAt)
            .FirstOrDefaultAsync(ct);

    public static async Task EndAsync(
        LeadsDbContext db, Guid conversationId, WhatsAppHandoverEnd how, Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        if (await FindOpenAsync(db, conversationId, ct) is { } open)
            open.End(how, by, now);
    }
}
