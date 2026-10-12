namespace Sankore.Modules.Integration.Features.Insurance.Complaints;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// The deadlines of the complaints mechanism, bound from <c>Integration:Insurance:Complaints</c>.
///
/// <para>
/// <b>The two periods are configurable and their defaults are NOT a quotation.</b> The règlement
/// n° 01/CIMA/PCMA/CE/SG/2024 requires a complaints-handling mechanism of anybody distributing
/// insurance by electronic means; I have not been able to read the periods it fixes, so the
/// defaults here — 5 days to acknowledge, 30 to answer — are the common West-African practice and
/// a placeholder, not the text. They are settings precisely so a deployment that has read the
/// article can set the real ones without a release, and this comment is where the uncertainty is
/// recorded rather than left to look like a verified figure.
/// </para>
///
/// <para>
/// A host setting and not a tenant parameter, like the retention floor: a deadline a tenant can
/// lengthen is a mechanism a tenant can opt out of.
/// </para>
/// </summary>
internal sealed class ComplaintOptions
{
    public const string SectionName = "Integration:Insurance:Complaints";

    /// <summary>Days to tell the customer the complaint is in hand.</summary>
    public int AcknowledgementDays { get; set; } = 5;

    /// <summary>Days to answer it.</summary>
    public int ResolutionDays { get; set; } = 30;

    /// <summary>
    /// Clamped rather than validated at boot, the same choice the retention options make: a typo
    /// in a configuration file must not take the API down, and a deadline of zero days would make
    /// every complaint overdue the moment it is recorded.
    /// </summary>
    internal int ResolvedAcknowledgementDays
        => AcknowledgementDays is > 0 and <= 90 ? AcknowledgementDays : 5;

    internal int ResolvedResolutionDays
        => ResolutionDays is > 0 and <= 365 && ResolutionDays >= ResolvedAcknowledgementDays
            ? ResolutionDays
            : Math.Max(30, ResolvedAcknowledgementDays);
}

/// <summary>
/// Issues the reference a customer quotes back: <c>REC-2026-000042</c>, per tenant and per year.
///
/// <para>
/// <b>Read-then-insert, and the unique index is what makes it true.</b> Two agents recording a
/// complaint in the same second would both read the same highest number;
/// <c>ux_ins_complaint_reference</c> refuses the second, and the caller retries. That is the same
/// bargain <c>CreateKycFileHandler</c> strikes with its own filtered unique index — the read keeps
/// the common case clean and the index keeps the rule true. EF InMemory honours no unique index,
/// so only the read half is exercised by the suite.
/// </para>
///
/// <para>
/// Per YEAR and not a single running sequence: a register is read a year at a time, and a
/// reference whose number says which year it belongs to is one an operator can place without a
/// lookup. The counter restarting each January is deliberate — the year is in the reference.
/// </para>
/// </summary>
internal static class ComplaintReference
{
    internal const string Prefix = "REC";

    internal static async Task<string> NextAsync(
        IntegrationDbContext db, Guid tenantId, int year, CancellationToken ct)
    {
        var prefix = $"{Prefix}-{year}-";

        // IgnoreQueryFilters with an explicit tenant predicate: this runs from a request and from
        // a consumer, and the ambient tenant is not trusted to be the one asked about.
        var highest = await db.InsuranceComplaints
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Reference.StartsWith(prefix))
            .OrderByDescending(c => c.Reference)
            .Select(c => c.Reference)
            .FirstOrDefaultAsync(ct);

        var next = 1;

        if (highest is { Length: > 0 }
            && int.TryParse(
                highest.AsSpan(prefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            next = parsed + 1;
        }

        // Six digits, zero-padded, so the string order IS the numeric order — which is what the
        // OrderByDescending above relies on. Past 999 999 complaints in one year the padding grows
        // and the ordering would break; a tenant at that volume has a different problem.
        return $"{prefix}{next:D6}";
    }
}
