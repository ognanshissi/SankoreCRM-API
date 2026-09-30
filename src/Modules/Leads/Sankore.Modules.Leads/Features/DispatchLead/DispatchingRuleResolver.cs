namespace Sankore.Modules.Leads.Features.DispatchLead;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Answers "which dispatching rule applies to this lead?".
///
/// It exists because the relation used to be inverted: the caller named a strategy and the
/// handler then looked for a rule matching THAT strategy. Nothing could therefore start from the
/// lead — which is what auto-dispatch on capture needs — and
/// <see cref="LeadSourceConfig.DefaultDispatchingRuleId"/>, though stored, exposed in the API and
/// updatable, was read by no dispatching code at all.
///
/// Resolution order:
/// <list type="number">
/// <item>the rule pinned on the lead source the lead was ingested through;</item>
/// <item>the active rule with the highest <c>Priority</c>;</item>
/// <item><see cref="DispatchingRule.Default"/>.</item>
/// </list>
/// </summary>
internal sealed class DispatchingRuleResolver(LeadsDbContext db)
{
    public async Task<DispatchingRule> ResolveAsync(
        Lead lead, DispatchingStrategy? requestedStrategy, CancellationToken ct)
    {
        // A caller that names a strategy explicitly still gets the rule tuned for it — the
        // manual dispatch screen must keep working exactly as before.
        if (requestedStrategy is { } strategy)
        {
            return await db.DispatchingRules
                       .Where(r => r.IsActive && r.Strategy == strategy)
                       .OrderByDescending(r => r.Priority)
                       .FirstOrDefaultAsync(ct)
                   ?? DispatchingRule.Default();
        }

        var pinned = await PinnedBySourceAsync(lead, ct);
        if (pinned is not null) return pinned;

        return await db.DispatchingRules
                   .Where(r => r.IsActive)
                   .OrderByDescending(r => r.Priority)
                   .FirstOrDefaultAsync(ct)
               ?? DispatchingRule.Default();
    }

    /// <summary>
    /// The rule configured on the lead source this lead came through, when it came through one.
    /// A <see cref="Lead"/> holds no source-config id — only the coarse <see cref="LeadSource"/>
    /// enum — so the link goes through <see cref="LeadIngestion"/>, which records both. Leads
    /// created by a file import or straight from the UI have no ingestion row and fall through.
    /// </summary>
    private async Task<DispatchingRule?> PinnedBySourceAsync(Lead lead, CancellationToken ct)
    {
        var ruleId = await db.LeadIngestions
            .Where(i => i.LeadId == lead.Id)
            .OrderByDescending(i => i.Id)
            .Join(db.LeadSourceConfigs,
                  ingestion => ingestion.SourceId,
                  source => source.Id,
                  (_, source) => source.DefaultDispatchingRuleId)
            .FirstOrDefaultAsync(ct);

        if (ruleId is null || ruleId == Guid.Empty) return null;

        return await db.DispatchingRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.IsActive, ct);
    }
}
