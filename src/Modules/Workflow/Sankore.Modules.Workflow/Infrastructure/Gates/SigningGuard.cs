using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel.Authorization;

namespace Sankore.Modules.Workflow.Infrastructure.Gates;

/// <summary>
/// Who may sign a step of a record the owning module holds to separate signers — a credit file
/// (2026-11-05, product owner). The engine only checks the step's role, so someone holding the
/// analyst's and the branch manager's roles could give the opinion and take the decision alone, and
/// a signer from another agency could decide on a client they cannot even read.
///
/// <para>
/// Applies to the person a step was handed to as well: an escalation or a delegation moves the work,
/// it does not lift the rule. Every other record keeps the role check alone.
/// </para>
/// </summary>
internal sealed class SigningGuard(
    WorkflowDbContext db,
    RecordStepGates gates,
    IAgencyScopeProvider agencyScope,
    ICustomersModule customers,
    ICurrentUser currentUser)
{
    public sealed record Candidate(Guid InstanceId, string EntityType, Guid EntityId);

    /// <summary>Why the caller may not sign this instance's open step, in French — or null.</summary>
    public async Task<string?> RefusalAsync(WorkflowInstance instance, CancellationToken ct)
    {
        var refusals = await RefusalsAsync(
            instance.TenantId, [new Candidate(instance.Id, instance.EntityType, instance.EntityId)], ct);
        return refusals.GetValueOrDefault(instance.Id);
    }

    /// <summary>The instances the caller may not sign, with why: a few reads whatever their number.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> RefusalsAsync(
        Guid tenantId, IReadOnlyCollection<Candidate> candidates, CancellationToken ct)
    {
        var refusals = new Dictionary<Guid, string>();
        if (candidates.Count == 0) return refusals;

        var userId = currentUser.Id;
        var instanceIds = candidates.Select(c => c.InstanceId).ToList();
        var signed = (await db.WorkflowInstanceSteps
                .AsNoTracking()
                .Where(s => instanceIds.Contains(s.InstanceId)
                         && s.ActedByUserId == userId
                         && s.Status != StepStatus.Pending
                         && s.Status != StepStatus.AwaitingApproval)
                .OrderBy(s => s.Order)
                .Select(s => new { s.InstanceId, s.Name })
                .ToListAsync(ct))
            .ToLookup(s => s.InstanceId, s => s.Name);

        // Reads of other modules, each on its own connection: inside the command's ambient
        // transaction they would ask for a distributed one.
        using var outside = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        var rules = new Dictionary<(string EntityType, Guid EntityId), RecordSigningRule>();
        foreach (var kind in candidates.GroupBy(c => c.EntityType, StringComparer.OrdinalIgnoreCase))
        {
            if (gates.Gate(kind.Key) is not { } gate) continue;
            var found = await gate.SigningRulesAsync(tenantId, kind.Select(c => c.EntityId).ToList(), ct);
            foreach (var candidate in kind)
                if (found.TryGetValue(candidate.EntityId, out var rule))
                    rules[(candidate.EntityType, candidate.EntityId)] = rule;
        }
        if (rules.Count == 0) return refusals;

        var clientIds = rules.Values.Where(r => r.ClientId.HasValue).Select(r => r.ClientId!.Value).Distinct().ToList();
        var clients = clientIds.Count == 0
            ? new Dictionary<Guid, ClientSummary>()
            : await customers.GetClientSummariesAsync(tenantId, clientIds, ct);

        // Agencies and accounts are the team's, shared with the test mode: the tenant of the data
        // would find no such user there, and a nobody sees nothing.
        var perimeter = await agencyScope.GetAccessibleAgencyIdsAsync(currentUser.TeamTenantId, userId, ct);

        foreach (var candidate in candidates)
        {
            if (!rules.TryGetValue((candidate.EntityType, candidate.EntityId), out var rule)) continue;

            // The client's agency first: it is the one that decides whether the signer can read the file.
            var agency = rule.ClientId is { } clientId && clients.TryGetValue(clientId, out var client)
                ? client.AgencyId
                : rule.AgencyId;

            if (rule.PreparedBy == userId)
                refusals[candidate.InstanceId] = "Vous suivez ce dossier de crédit : une autre personne doit signer cette étape.";
            else if (signed[candidate.InstanceId].LastOrDefault() is { } name)
                refusals[candidate.InstanceId] = $"Vous avez déjà signé l'étape « {name} » de ce dossier de crédit : une autre personne doit signer celle-ci.";
            else if (agency is { } required && perimeter is not null && !perimeter.Contains(required))
                refusals[candidate.InstanceId] = "Ce dossier de crédit relève d'une agence hors de votre périmètre : vous ne pouvez pas le signer.";
        }
        return refusals;
    }
}
