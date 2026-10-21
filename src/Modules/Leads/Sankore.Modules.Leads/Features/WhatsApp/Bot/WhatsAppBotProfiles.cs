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
    public async Task<WhatsAppBotProfile?> ResolveActiveAsync(Guid tenantId, Lead? lead, CancellationToken ct)
    {
        if (lead is null || !model.IsAvailable) return null;
        if (!await settings.GetBoolAsync(tenantId, LeadSettingKeys.WhatsAppBotEnabled, ct)) return null;

        var profile = (lead.ProductLineId is { } line && line != Guid.Empty ? await LatestAsync(line, ct) : null)
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
