namespace Sankore.Modules.Leads.Features.DispatchingRules;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Gives every tenant one visible dispatching rule, carrying exactly the values
/// <see cref="DispatchingRule.Default"/> already applied invisibly.
///
/// Dispatching never needed a configured rule — the resolver falls back to
/// <c>DispatchingRule.Default()</c>, which is a complete rule rather than a null. The problem was
/// that the fallback could not be seen: an administrator opening the rules screen found an empty
/// list and concluded nothing was configured, while their leads were being routed with a 2-hour
/// SLA and a 20-task ceiling they had neither chosen nor could adjust — and
/// <c>LeadAssignment.RuleId</c> stayed null, so the history did not record which parameters had
/// produced an assignment.
///
/// Behaviour is therefore unchanged by design; only its visibility and its traceability are new.
/// </summary>
internal static class DispatchingRuleSeeder
{
    public const string DefaultRuleName = "Par défaut";

    public static async Task SeedAsync(
        LeadsDbContext db,
        ITenantStore tenantStore,
        ILogger logger,
        CancellationToken ct = default)
    {
        var tenants = await tenantStore.GetAllActiveAsync(ct);
        if (tenants.Count == 0) return;

        // One query for every tenant rather than one per tenant.
        var tenantsWithARule = await db.DispatchingRules
            .IgnoreQueryFilters()
            .Select(r => r.TenantId)
            .Distinct()
            .ToListAsync(ct);

        var seeded = 0;

        foreach (var tenant in tenants)
        {
            // Seeded only for a tenant that has NO rule at all. A tenant which configured its
            // own rules has made its choice — adding a default beside them would change which
            // rule wins on priority, and re-adding one after a deliberate deletion would be a
            // startup that undoes an administrator's decision.
            if (tenantsWithARule.Contains(tenant.Id)) continue;

            var template = DispatchingRule.Default();

            db.DispatchingRules.Add(DispatchingRule.Create(
                tenantId:              tenant.Id,
                name:                  DefaultRuleName,
                strategy:              template.Strategy,
                weights:               template.Weights,
                maxLeadsPerAgent:      template.MaxLeadsPerAgent,
                antiMonopolyThreshold: template.AntiMonopolyThreshold,
                firstContactSla:       template.FirstContactSla,
                priority:              0,
                maxTasksPerAgent:      template.MaxTasksPerAgent,
                declineExclusionTtl:   template.DeclineExclusionTtl));

            seeded++;
        }

        if (seeded == 0) return;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Seeded the \"{RuleName}\" dispatching rule for {Count} tenant(s) that had none",
            DefaultRuleName, seeded);
    }
}
