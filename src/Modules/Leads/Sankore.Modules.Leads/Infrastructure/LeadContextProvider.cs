using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Workflow;

namespace Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Hydrates the workflow context dictionary with Lead-specific fields so that
/// transition conditions and step rules can evaluate values like Score or Status
/// without the Workflow module knowing about the Leads domain.
/// </summary>
internal sealed class LeadContextProvider(LeadsDbContext db) : IContextProvider
{
    public string EntityType => "Lead";

    public async Task<Dictionary<string, object>> BuildAsync(
        Guid entityId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var lead = await db.Leads
            .IgnoreQueryFilters()
            .Where(l => l.Id == entityId && l.TenantId == tenantId)
            .Select(l => new
            {
                Status            = l.Status.ToString(),
                Score             = l.Score,
                Source            = l.Source.ToString(),
                InterestedProduct = l.InterestedProduct,
                PreferredLanguage = l.PreferredLanguage,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (lead is null) return [];

        return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"]            = lead.Status,
            ["score"]             = (double)lead.Score,
            ["source"]            = lead.Source,
            ["interestedProduct"] = lead.InterestedProduct ?? string.Empty,
            ["preferredLanguage"] = lead.PreferredLanguage ?? string.Empty,
        };
    }
}
