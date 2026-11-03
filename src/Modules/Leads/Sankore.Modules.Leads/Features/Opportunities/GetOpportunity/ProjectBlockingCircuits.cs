namespace Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>
/// The circuits holding a project's win, told as the agent needs them (2026-11-02, product owner):
/// the step open now, who decides it by name, what it waits for on the record and, for a
/// « Tâche manuelle », the task whose completion passes it. The banner named the step only.
/// </summary>
internal static class ProjectBlockingCircuits
{
    public static async Task<IReadOnlyList<ProjectBlockingCircuitDto>> ReadAsync(
        IReadOnlyList<BlockingCircuit> circuits, Guid tenantId, LeadsDbContext db,
        IAdministrationModule? administration, ICurrentUser? me, CancellationToken ct)
    {
        if (circuits.Count == 0) return [];
        var open = circuits.Select(c => c.OpenStep).OfType<BlockingOpenStep>().ToList();

        var stepIds = open.Select(s => s.InstanceStepId).ToList();
        var tasks = stepIds.Count == 0 ? [] : await db.CrmTasks
            .Where(t => t.TriggerEventType == "workflow.step" && t.TriggerEventId != null && stepIds.Contains(t.TriggerEventId.Value))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, StepId = t.TriggerEventId!.Value, t.Title, t.Status, t.AssignedAgentId })
            .ToListAsync(ct);
        var taskByStep = tasks.GroupBy(t => t.StepId).ToDictionary(g => g.Key, g => g.First());

        var roleCodes = open.Select(s => s.ApproverRoleCode).OfType<string>().Distinct().ToList();
        var userIds = open.Select(s => s.AssignedToUserId).OfType<Guid>()
            .Concat(tasks.Select(t => t.AssignedAgentId).OfType<Guid>())
            .Distinct().ToList();
        var roles = administration is null || roleCodes.Count == 0
            ? new Dictionary<string, string>()
            : await administration.GetRoleLabelsAsync(tenantId, roleCodes, ct);
        var names = administration is null || userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await administration.GetUserNamesAsync(tenantId, userIds, ct);

        var mayApprove = me?.Permissions.Contains("workflow:approve", StringComparer.OrdinalIgnoreCase) == true
                      || me?.Roles.Contains("Administrator", StringComparer.OrdinalIgnoreCase) == true
                      || me?.Roles.Contains("System", StringComparer.OrdinalIgnoreCase) == true;

        return circuits.Select(c =>
        {
            if (c.OpenStep is not { } s) return new ProjectBlockingCircuitDto(c.InstanceId, c.Circuit, c.BlockingSteps, null);

            var canAct = me is not null && mayApprove
                      && (s.AssignedToUserId == me.Id
                          || s.ApproverRoleCode is null
                          || me.Roles.Contains(s.ApproverRoleCode, StringComparer.OrdinalIgnoreCase));
            var task = taskByStep.GetValueOrDefault(s.InstanceStepId) is { } t
                ? new ProjectStepTaskDto(t.Id, t.Title, t.Status.ToString(),
                    t.AssignedAgentId is { } a ? names.GetValueOrDefault(a) : null)
                : null;

            return new ProjectBlockingCircuitDto(c.InstanceId, c.Circuit, c.BlockingSteps, new ProjectBlockingStepDto(
                s.Name, s.Kind,
                s.ApproverRoleCode is { } code ? roles.GetValueOrDefault(code) ?? code : null,
                s.AssignedToUserId is { } u ? names.GetValueOrDefault(u) : null,
                canAct, s.DueAt,
                s.Requirements.Select(r => new ProjectBlockingRequirementDto(r.Label, r.Satisfied, r.FieldKey)).ToList(),
                s.CompleteOnEntityType, s.CompleteOnEntityId, task));
        }).ToList();
    }
}
