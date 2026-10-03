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
/// <item>the rule pinned on the source the lead was ingested through
///       (<see cref="Lead.LeadSourceConfigId"/>);</item>
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
    /// The rule configured on the source this lead came through, when it came through one.
    /// <see cref="Lead.LeadSourceConfigId"/> is null for a lead typed into the UI, imported from
    /// a file, or produced by a merge — those fall straight through to priority, at no query cost.
    ///
    /// This used to join <see cref="LeadIngestion"/> to reach the source, because a lead carried
    /// no source-config id. That cost a round trip on EVERY strategy-less dispatch, including the
    /// majority of leads which have no ingestion row and so learned nothing from it. Reading the
    /// column directly also removes a subtlety the join got wrong: it ordered by
    /// <c>LeadIngestion.Id</c>, a random v4 Guid rather than the chronological
    /// <c>IngestedAt</c>, and ordered before the join rather than after — so "the most recent
    /// ingestion wins" was never what the SQL actually expressed.
    ///
    /// The id is an opaque reference with no foreign key, so it can outlive the source it names;
    /// an id that resolves to nothing simply falls through, same as no id at all.
    /// </summary>
    private async Task<DispatchingRule?> PinnedBySourceAsync(Lead lead, CancellationToken ct)
    {
        if (lead.LeadSourceConfigId is not { } sourceId) return null;

        var ruleId = await db.LeadSourceConfigs
            .Where(s => s.Id == sourceId)
            .Select(s => s.DefaultDispatchingRuleId)
            .FirstOrDefaultAsync(ct);

        // Covers three cases at once: the source pins no rule, the source no longer exists, and
        // a rule id of Guid.Empty (what DispatchingRule.Default() carries, never a stored rule).
        if (ruleId is null || ruleId == Guid.Empty) return null;

        return await db.DispatchingRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.IsActive, ct);
    }
}
