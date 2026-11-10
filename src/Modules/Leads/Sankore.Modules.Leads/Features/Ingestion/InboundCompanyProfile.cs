namespace Sankore.Modules.Leads.Features.Ingestion;

using System.Globalization;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The company a web form or a webhook describes (key accounts lot 1, 2026-11-09). Before, an inbound
/// payload could carry neither the company nor the prospect type: a B2B lead from the website arrived
/// as a person.
///
/// <para>
/// BEST EFFORT, like the custom fields on this path: an unknown sector or an unreadable headcount is
/// logged and dropped, the prospect is kept. « Entreprise » / « Particulier » are read as well as the
/// enum names, since a form's select says them in French.
/// </para>
/// </summary>
internal static class InboundCompanyProfile
{
    /// <summary>The mapping targets this helper reads, beside the ones the factory reads.</summary>
    public static readonly IReadOnlyList<string> Fields =
    [
        "prospectType", "companyName", "companyEmail", "companyPhone", "website",
        "activitySector", "companySize", "estimatedAnnualPurchases", "contactJobTitle",
    ];

    public static async Task ApplyAsync(
        Lead lead,
        IReadOnlyDictionary<string, string?> mapped,
        IInstitutionCurrency? institutionCurrency,
        ILogger? logger,
        CancellationToken ct)
    {
        string? Get(string key) => mapped.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var companyName = Get("companyName");
        var type = ParseType(Get("prospectType"));
        if (type is null && Get("prospectType") is { } unreadable)
            logger?.LogWarning("Inbound lead {LeadId}: prospect type '{Value}' ignored.", lead.Id, unreadable);

        // A payload naming a company is a company prospect unless it says otherwise — as an import row.
        var effective = type ?? (companyName is not null ? LeadType.Corporate : (LeadType?)null);
        if (effective is { } t && t != lead.ProspectType)
            lead.ChangeProspectType(t);

        if (companyName is not null || Get("companyEmail") is not null || Get("companyPhone") is not null || Get("website") is not null)
            lead.UpdateCompany(companyName, Get("companyEmail"), Get("companyPhone"), Get("website"));

        var sector = Get("activitySector");
        if (sector is not null && LeadActivitySectors.Normalize(sector) is null)
        {
            logger?.LogWarning("Inbound lead {LeadId}: activity sector '{Value}' ignored.", lead.Id, sector);
            sector = null;
        }

        var size = Get("companySize");
        if (size is not null && LeadCompanySizes.Parse(size) is null)
        {
            logger?.LogWarning("Inbound lead {LeadId}: company size '{Value}' ignored.", lead.Id, size);
            size = null;
        }

        var jobTitle = Get("contactJobTitle");
        if (jobTitle is { Length: > 150 }) jobTitle = jobTitle[..150];

        if (sector is not null || size is not null || jobTitle is not null)
            lead.DescribeCompany(sector, size, jobTitle);

        if (Get("estimatedAnnualPurchases") is { } rawPurchases)
        {
            var normalized = rawPurchases.Replace(" ", "").Replace(" ", "").Replace(" ", "");
            if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0)
            {
                var currency = await institutionCurrency.ResolveAsync(lead.TenantId, null, null, ct);
                if (currency.IsSuccess)
                    lead.SetEstimatedAnnualPurchases(new Money(amount, currency.Value));
            }
            else
                logger?.LogWarning("Inbound lead {LeadId}: yearly purchases '{Value}' ignored.", lead.Id, rawPurchases);
        }
    }

    private static LeadType? ParseType(string? value)
    {
        if (value is null) return null;
        if (value.Equals("Entreprise", StringComparison.OrdinalIgnoreCase)) return LeadType.Corporate;
        if (value.Equals("Particulier", StringComparison.OrdinalIgnoreCase)) return LeadType.Individual;
        return Enum.TryParse<LeadType>(value, ignoreCase: true, out var t) && Enum.IsDefined(t) && !int.TryParse(value, out _)
            ? t : null;
    }
}
