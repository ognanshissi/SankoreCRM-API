using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Notifications;

namespace Sankore.Modules.Workflow.Infrastructure.Jobs;

/// <summary>
/// What happens when an open step's delay runs out and its circuit draws no TIMEOUT transition —
/// the step's « Escalade » (product owner, 2026-10-29):
/// <list type="bullet">
/// <item><c>supervisor</c>: handed to the line manager of whoever holds it (else of the record's
/// agent), else to their agency's head;</item>
/// <item><c>reassign</c>: handed to another active holder of its role, in the record's agency or above;</item>
/// <item><c>supervisor_then_reassign</c>: the first, then the second at the next overrun;</item>
/// <item><c>none</c>, or nobody found: it stays where it was and its people are reminded.</item>
/// </list>
/// Never ends the instance — it used to, in « Délai dépassé », with nobody told. Each time, a fresh
/// delay, a line in the history and a bell for whoever must act.
/// </summary>
internal sealed class StepEscalator(
    WorkflowDbContext db,
    RecordFactsReader facts,
    IAdministrationModule administration,
    INotificationsModule notifications,
    TimeProvider clock,
    ILogger<StepEscalator> logger)
{
    public const string Category = "workflow.escalation";

    public async Task EscalateAsync(Guid instanceId, CancellationToken ct)
    {
        var instance = await db.WorkflowInstances
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(i => i.Steps)
            .FirstOrDefaultAsync(i => i.Id == instanceId, ct);
        var step = instance?.Steps.FirstOrDefault(s => s.Order == instance.CurrentStepOrder && s.Status == StepStatus.AwaitingApproval);
        if (instance is null || step is null) return;

        var record = await facts.ReadAsync(instance.TenantId, instance.EntityType, instance.EntityId, ct);
        var mode = Mode(step.ConfigJson);
        if (mode == "supervisor_then_reassign")
        {
            var escalations = await db.WorkflowAuditEntries
                .CountAsync(a => a.InstanceId == instance.Id && a.FromStateId == step.StepDefinitionId
                              && a.EventCode == EventCodes.Escalated, ct);
            mode = escalations == 0 ? "supervisor" : "reassign";
        }

        Guid? target = mode switch
        {
            "supervisor" => await SupervisorAsync(instance.TenantId, step.AssignedToUserId ?? record?.AgentId, ct),
            "reassign" => await OtherRoleHolderAsync(instance.TenantId, step, record?.AgencyId, ct),
            _ => null,
        };

        string comment;
        IReadOnlyCollection<Guid> told;
        if (target is { } user)
        {
            var name = (await administration.GetUserNamesAsync(instance.TenantId, [user], ct)).GetValueOrDefault(user) ?? "un autre responsable";
            comment = mode == "supervisor"
                ? $"Délai dépassé : étape confiée à {name} (supérieur hiérarchique)."
                : $"Délai dépassé : étape réassignée à {name}.";
            told = [user];
        }
        else
        {
            comment = mode == "none"
                ? "Délai dépassé : responsables relancés."
                : "Délai dépassé : personne à qui confier l'étape, responsables relancés.";
            told = step.AssignedToUserId is { } holder ? [holder]
                : step.ApproverRoleCode is { } role
                    ? await administration.ListActiveUsersInRolesAsync(instance.TenantId, [role], record?.AgencyId, ct)
                    : [];
        }

        instance.EscalateCurrentStep(target, comment, clock.GetUtcNow());
        db.WorkflowAuditEntries.AddRange(instance.PendingAuditEntries);
        await db.SaveChangesAsync(ct);

        foreach (var recipient in told)
        {
            await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                instance.TenantId, recipient, Category,
                $"Étape en retard : « {step.Name} »",
                comment,
                record?.Link,
                $"workflow.escalation:{step.Id}:{step.DueAt:O}:{recipient}"), ct);
        }

        logger.LogInformation("Step {StepId} of instance {InstanceId} escalated: {Comment}", step.Id, instance.Id, comment);
    }

    private async Task<Guid?> SupervisorAsync(Guid tenantId, Guid? of, CancellationToken ct)
    {
        if (of is not { } user) return null;
        var supervisors = await administration.GetUserSupervisorsAsync(tenantId, user, ct);
        return supervisors.LineManagerId ?? supervisors.AgencyManagerId;
    }

    /// <summary>Another holder of the step's role than whoever has it — the first in a stable order.</summary>
    private async Task<Guid?> OtherRoleHolderAsync(Guid tenantId, WorkflowInstanceStep step, Guid? agencyId, CancellationToken ct)
    {
        if (step.ApproverRoleCode is not { } role) return null;
        var holders = await administration.ListActiveUsersInRolesAsync(tenantId, [role], agencyId, ct);
        return holders.Where(h => h != step.AssignedToUserId).OrderBy(h => h).Cast<Guid?>().FirstOrDefault();
    }

    /// <summary>The step's « escalation » (<c>none</c> when unset or unreadable).</summary>
    public static string Mode(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return "none";
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("escalation", out var e)
                && e.ValueKind == JsonValueKind.String
                && e.GetString() is "supervisor" or "reassign" or "supervisor_then_reassign"
                    ? e.GetString()!
                    : "none";
        }
        catch (JsonException)
        {
            return "none";
        }
    }
}
