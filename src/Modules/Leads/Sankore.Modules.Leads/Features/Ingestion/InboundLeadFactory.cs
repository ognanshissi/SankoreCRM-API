namespace Sankore.Modules.Leads.Features.Ingestion;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The steps of the inbound pipeline that run once a payload has been mapped to lead fields:
/// required-field check, phone dedup and lead construction. Shared by
/// <see cref="IngestInboundLeadHandler"/> and the manual reprocessing of a failed ingestion, so
/// that a lead completed by hand is built exactly like one that arrived complete.
/// </summary>
internal static class InboundLeadFactory
{
    /// <summary>
    /// The lead fields <see cref="Build"/> actually reads. Narrower than
    /// <c>FieldMappingEngine.ValidTargetFields</c>: gender, date of birth, desired amount and the
    /// social ids are valid mapping targets that this path does not carry onto the lead, so a
    /// manual link to one of them would be accepted and silently dropped.
    /// </summary>
    public static readonly IReadOnlySet<string> CapturedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "phoneNumber", "fullName", "firstName", "lastName", "email",
        "interestedProduct", "preferredLanguage", "nationalId", "customerReference",
        "comment", "campaign", "externalReference", "externalId",
        "utmSource", "utmMedium", "utmCampaign", "utmContent", "utmTerm",
        "landingPage", "referrer", "latitude", "longitude",
    };

    /// <summary>
    /// Tokens of the required fields still missing, as stored after <c>REQUIRED_FIELDS_MISSING:</c>.
    /// The name token names the three fields any ONE of which would satisfy it.
    /// </summary>
    public static IReadOnlyList<string> MissingRequiredFields(
        IReadOnlyDictionary<string, string?> mapped, out string? phone, out string? fullName)
    {
        mapped.TryGetValue("phoneNumber", out phone);
        mapped.TryGetValue("fullName", out var suppliedFullName);
        mapped.TryGetValue("firstName", out var suppliedFirstName);
        mapped.TryGetValue("lastName", out var suppliedLastName);

        fullName = CaptureLead.LeadNameResolver.Resolve(
            suppliedFullName, suppliedFirstName, suppliedLastName);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(phone)) missing.Add("phoneNumber");
        if (string.IsNullOrWhiteSpace(fullName)) missing.Add("fullName|firstName|lastName");
        return missing;
    }

    /// <summary>An open lead with the same phone captured within the source's dedup window.</summary>
    public static async Task<Guid?> FindDuplicateAsync(
        LeadsDbContext db, Guid tenantId, string phoneIndex, LeadSourceConfig source,
        TimeProvider clock, CancellationToken ct)
    {
        var dedupCutoff = clock.GetUtcNow().AddDays(-source.DedupWindowDays);

        var duplicate = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.TenantId == tenantId
                        && l.PhoneBlindIndex == phoneIndex
                        && l.Status != LeadStatus.Lost
                        && l.Status != LeadStatus.Archived
                        && l.Status != LeadStatus.Disqualified
                        && l.CapturedAt >= dedupCutoff)
            .Select(l => new { l.Id })
            .FirstOrDefaultAsync(ct);

        return duplicate?.Id;
    }

    public static Lead Build(
        Guid tenantId,
        LeadSourceConfig source,
        IReadOnlyDictionary<string, string?> mapped,
        string fullName,
        string phone,
        string phoneIndex,
        string? ingestionExternalId,
        TimeProvider clock)
    {
        string? Get(string key) => mapped.TryGetValue(key, out var v) ? v : null;

        return Lead.Capture(
            tenantId:          tenantId,
            fullName:          fullName,
            phoneNumber:       phone,
            // The coarse enum stays Web for every inbound source: it feeds
            // LeadScoreCalculator.SourceQualityScore, whose thresholds are measured and
            // documented, so changing it here would move auto-qualification outcomes. The real
            // source is leadSourceConfigId below.
            source:            LeadSource.Web,
            interestedProduct: Get("interestedProduct") ?? "Unknown",
            preferredLanguage: Get("preferredLanguage") ?? "FR",
            location:          new GeoPoint(ParseCoordinate(Get("latitude")), ParseCoordinate(Get("longitude"))),
            preferredAgencyId: null,
            clock:             clock,
            firstName:         Get("firstName"),
            lastName:          Get("lastName"),
            email:             Get("email"),
            nationalId:        Get("nationalId"),
            customerReference: Get("customerReference"),
            comment:           Get("comment"),
            campaign:          Get("campaign"),
            externalReference: Get("externalReference"),
            externalId:        Get("externalId") ?? ingestionExternalId,
            phoneBlindIndex:   phoneIndex,
            utmSource:         Get("utmSource"),
            utmMedium:         Get("utmMedium"),
            utmCampaign:       Get("utmCampaign"),
            utmContent:        Get("utmContent"),
            utmTerm:           Get("utmTerm"),
            landingPage:       Get("landingPage"),
            referrer:          Get("referrer"),
            acquisitionCost:   source.CostPerLead,
            isTest:            source.Status == LeadSourceStatus.Testing,
            // Safe to trust: the caller loaded and tenant-checked the config.
            leadSourceConfigId: source.Id);
    }

    /// <summary>
    /// A coordinate arrives here as JSON or form text, which always uses a decimal POINT —
    /// the culture of the host that happens to run the process is irrelevant to it. The
    /// parameterless <c>double.TryParse</c> used the current culture, so on a fr-FR host
    /// "5.300489" failed to parse and the lead silently landed on (0, 0); the same value
    /// parsed correctly on an invariant-culture machine, so no test caught it. This is the
    /// rule <see cref="Import.LeadRowParser"/> already applies to imported rows.
    /// </summary>
    private static double ParseCoordinate(string? raw)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
