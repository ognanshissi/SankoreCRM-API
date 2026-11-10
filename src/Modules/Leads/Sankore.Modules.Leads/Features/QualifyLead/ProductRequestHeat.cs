namespace Sankore.Modules.Leads.Features.QualifyLead;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProductLines;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// « Un lead qui remplit un formulaire pour un produit donné est un lead chaud » (key accounts lot 3,
/// 2026-11-09, product owner). A lead that came in through a source whose option is on
/// (<see cref="LeadSourceConfig.EffectiveProductRequestIsHot"/>, by default the form channels) and
/// asks for an ACTIVE catalogue product is marked (<see cref="Lead.ProductRequestAt"/>), and reads at
/// least « Chaud » — the score is untouched — while the request is fresh.
///
/// <para>
/// It stops being fresh, and the temperature goes back to the score's, when a contact ended « Pas
/// intéressé », when the lead is closed, or after <see cref="Lapse"/> without activity — the first step
/// of the cooling: a request nobody answered for a month is no longer an emergency. The nightly cooling
/// rescores such a lead and drops it.
/// </para>
/// </summary>
internal static class ProductRequestHeat
{
    public static readonly TimeSpan Lapse = TimeSpan.FromDays(30);

    private static readonly LeadStatus[] Closed =
        [LeadStatus.Converted, LeadStatus.Archived, LeadStatus.Lost, LeadStatus.Disqualified];

    /// <summary>
    /// Marks a just-captured lead when its source and its product say so. Best effort: a catalogue
    /// that cannot be read leaves the lead unmarked, never refuses it.
    /// </summary>
    public static async Task MarkAsync(
        LeadsDbContext db, IAdministrationModule? administration, Lead lead, DateTimeOffset now, CancellationToken ct)
    {
        if (administration is null || lead.LeadSourceConfigId is not { } sourceId) return;
        if (string.IsNullOrWhiteSpace(lead.InterestedProduct) || lead.InterestedProduct == "Unknown") return;

        var source = await db.LeadSourceConfigs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Id == sourceId && s.TenantId == lead.TenantId)
            .Select(s => new { s.ProductRequestIsHot, s.ChannelType })
            .FirstOrDefaultAsync(ct);
        if (source is null || !(source.ProductRequestIsHot ?? LeadSourceConfig.DefaultProductRequestIsHot(source.ChannelType)))
            return;

        if (await ProductLineLookup.ResolveAsync(administration, lead.TenantId, lead.InterestedProduct, ct) is null)
            return;

        lead.MarkProductRequest(now);
    }

    /// <summary>Whether the lead's product request still makes it hot.</summary>
    public static async Task<bool> IsActiveAsync(LeadsDbContext db, Lead lead, DateTimeOffset now, CancellationToken ct)
    {
        if (lead.ProductRequestAt is not { } at || Closed.Contains(lead.Status)) return false;

        var lastTouch = lead.LastActivityAt is { } last && last > at ? last : at;
        if (now - lastTouch >= Lapse) return false;

        var refused = await db.LeadActivities.AnyAsync(a => a.LeadId == lead.Id
                                                         && a.Outcome == ActivityOutcome.NotInterested
                                                         && a.PerformedAt >= at, ct);
        return !refused;
    }
}
