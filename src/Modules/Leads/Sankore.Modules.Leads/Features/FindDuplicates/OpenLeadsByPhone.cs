namespace Sankore.Modules.Leads.Features.FindDuplicates;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;

/// <summary>
/// The OPEN leads of a tenant whose telephone number is this one — newest activity first.
///
/// <para>
/// One definition for its two callers: the telephony module's caller-id lookup, through
/// <c>ILeadsModule.FindOpenLeadsByPhoneAsync</c>, and the attachment of an inbound WhatsApp
/// message (WA-BE-09), which runs inside this module. Two copies would eventually disagree about
/// which lead a number belongs to, and the two channels would then file one customer's call and
/// message on different records.
/// </para>
/// </summary>
internal static class OpenLeadsByPhone
{
    public static async Task<IReadOnlyList<LeadPhoneMatch>> FindAsync(
        LeadsDbContext db,
        IPhoneBlindIndexer phoneBlindIndexer,
        Guid tenantId,
        string phoneNumber,
        CancellationToken ct)
    {
        var trimmed = phoneNumber.Trim();
        var phoneIndex = phoneBlindIndexer.Compute(trimmed);
        var phoneDigits = IdentityMatchScorer.NormalizePhone(trimmed);

        // A number that normalises to nothing — punctuation, a provider's "anonymous" marker —
        // would make the legacy branch an EndsWith("") that matches every un-backfilled lead in
        // the tenant. An incoming call with no caller id is ordinary, so it answers empty.
        if (phoneDigits.Length == 0)
            return [];

        return await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                     && !l.IsAnonymized
                     && l.Status != LeadStatus.Converted
                     && l.Status != LeadStatus.Lost
                     && l.Status != LeadStatus.Disqualified
                     && l.Status != LeadStatus.Archived
                     && ((l.PhoneBlindIndex != null && l.PhoneBlindIndex == phoneIndex)
                      || (l.PhoneBlindIndex == null && l.PhoneNumber.EndsWith(phoneDigits))))
            .OrderByDescending(l => l.LastActivityAt ?? l.CapturedAt)
            .Select(l => new LeadPhoneMatch(
                l.Id,
                l.FullName,
                l.Status.ToString(),
                l.AgencyId,
                l.CurrentAssignedId))
            .ToListAsync(ct);
    }
}
