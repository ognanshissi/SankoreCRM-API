namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Which bot profile answers a lead, if any (WA-BE-12) — the single answer read by the inbound
/// handler, the engine and the window sweep, so the three cannot disagree on whether the bot is on.
///
/// <para>
/// <b>Four switches, all required.</b> The deployment has a model; the tenant turned the bot on
/// (<c>whatsapp-bot-enabled</c>); the conversation is filed on a lead; and the profile that
/// applies to that lead is enabled. The profile that applies is the lead's product line's when
/// that line HAS a profile — enabled or not: a product whose bot an administrator switched off
/// must not be answered by the general profile instead — and the general profile otherwise.
/// </para>
///
/// <para>
/// <b>A client's or an unattached conversation gets no bot.</b> A client writing is somebody the
/// institution already serves, whose questions are about their accounts; and an unattached number
/// is a conversation the tenant chose not to turn into a lead. Both go to a person.
/// </para>
/// </summary>
internal sealed class WhatsAppBotProfiles(LeadsDbContext db, ILeadSettings settings, IWhatsAppBotModel model)
{
    public Task<WhatsAppBotProfile?> ResolveActiveAsync(Guid tenantId, Lead? lead, CancellationToken ct) =>
        lead is null ? Task.FromResult<WhatsAppBotProfile?>(null) : ResolveAsync(tenantId, lead.ProductLineId, ct);

    /// <summary>
    /// The profile for a conversation: its lead's, or — for a prospect the bot talks to before any
    /// lead exists — the profile of the product the customer already named, else the general one.
    /// An unattached conversation that is NOT collecting a prospect still gets none.
    /// </summary>
    public Task<WhatsAppBotProfile?> ResolveForConversationAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, CancellationToken ct) =>
        lead is not null ? ResolveAsync(tenantId, lead.ProductLineId, ct)
        : conversation is { CollectingProspect: true, LeadId: null }
            ? ResolveAsync(tenantId, WhatsAppBotFieldValues.PendingProduct(conversation.PendingLeadFields()), ct)
            : Task.FromResult<WhatsAppBotProfile?>(null);

    /// <summary>
    /// Whether the bot would answer a number nobody knows yet — what decides, at the first message,
    /// whether its lead can wait for the bot to collect the minimum.
    /// </summary>
    public Task<WhatsAppBotProfile?> ResolveForUnknownNumberAsync(Guid tenantId, CancellationToken ct) =>
        ResolveAsync(tenantId, null, ct);

    private async Task<WhatsAppBotProfile?> ResolveAsync(Guid tenantId, Guid? productLineId, CancellationToken ct)
    {
        if (!model.IsAvailable) return null;
        if (!await settings.GetBoolAsync(tenantId, LeadSettingKeys.WhatsAppBotEnabled, ct)) return null;

        var profile = (productLineId is { } line && line != Guid.Empty ? await LatestAsync(line, ct) : null)
                      ?? await LatestAsync(Guid.Empty, ct);

        return profile is { IsEnabled: true } ? profile : null;
    }

    /// <summary>The current version of a line's profile (<see cref="Guid.Empty"/> = general), or null when never saved.</summary>
    public Task<WhatsAppBotProfile?> LatestAsync(Guid productLineId, CancellationToken ct) =>
        db.WhatsAppBotProfiles
            .Where(p => p.ProductLineId == productLineId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);
}
