namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Transfère au remplaçant les tâches CRM encore ouvertes d'un agent qui déclare une absence, quand
/// il l'a demandé. Chaque transfert laisse une ligne <see cref="TaskReassignment"/>, comme une
/// réassignation manuelle : le motif cite la période, l'acteur est l'agent lui-même.
///
/// Idempotent sans table d'inbox : une seconde livraison ne trouve plus de tâche ouverte au nom de
/// l'absent. Les tâches ne lui reviennent pas automatiquement à son retour.
///
/// Pas d'ITenantContext dans un consumer : IgnoreQueryFilters + TenantId du message.
/// </summary>
public sealed class AgentAbsenceTaskTransferConsumer(
    LeadsDbContext db,
    ILogger<AgentAbsenceTaskTransferConsumer> logger)
    : IConsumer<AgentAbsenceDeclaredEvent>
{
    public async Task Consume(ConsumeContext<AgentAbsenceDeclaredEvent> context)
    {
        var evt = context.Message;
        if (!evt.TransferTasks || evt.ReplacementUserId is not { } replacementId || replacementId == evt.AgentId)
            return;

        var ct = context.CancellationToken;
        var tasks = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == evt.TenantId
                     && t.AssignedAgentId == evt.AgentId
                     && (t.Status == CrmTaskStatus.Pending || t.Status == CrmTaskStatus.InProgress))
            .ToListAsync(ct);

        if (tasks.Count == 0) return;

        var reason = $"Absence du {evt.From:dd/MM/yyyy} au {evt.To:dd/MM/yyyy}";
        foreach (var task in tasks)
        {
            var result = task.Reassign(replacementId);
            if (result.IsFailure) continue;

            db.TaskReassignments.Add(TaskReassignment.Create(
                tenantId:        task.TenantId,
                taskId:          task.Id,
                previousAgentId: result.Value,
                newAgentId:      replacementId,
                reason:          reason,
                actorId:         evt.AgentId,
                slaExtended:     false,
                newSlaDeadline:  null));
        }

        // Le cache de charge (AgentCapacityService, interne au module et donc hors de portée d'un
        // consumer public) expire seul en 30 secondes.
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Transferred {Count} open task(s) from absent agent {AgentId} to {ReplacementId} (tenant {TenantId}).",
            tasks.Count, evt.AgentId, replacementId, evt.TenantId);
    }
}
