namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead.Events;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// Gives the agent a task when a lead is dispatched to them: one per active <c>LeadDispatched</c>
/// generation rule when the tenant configured some, and otherwise the default first-contact task
/// (<see cref="AssignmentTasks"/>). It also cancels the open assignment tasks of the agent the lead
/// came from.
///
/// <para>
/// It used to do nothing at all for a tenant without rules — the default — so an assignment never
/// reached the agent's "Mes tâches" list, which is the one screen an agent works from.
/// </para>
///
/// Note: no ITenantContext available in MassTransit consumers (no HTTP context); every query
/// uses IgnoreQueryFilters() + the TenantId from the event payload.
/// </summary>
public sealed class LeadDispatchedTaskConsumer(
    LeadsDbContext db,
    ILogger<LeadDispatchedTaskConsumer> logger)
    : IConsumer<LeadDispatchedEvent>
{
    public async Task Consume(ConsumeContext<LeadDispatchedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;

        var rules = await db.TaskGenerationRules
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == evt.TenantId
                     && r.TriggerEventType == TaskTriggerEvents.LeadDispatched
                     && r.IsActive)
            .ToListAsync(ct);

        var created = await AssignmentTasks.SyncOnAssignedAsync(
            db, evt.TenantId, evt.LeadId, evt.AgentId, evt.SlaDeadline, rules, DateTimeOffset.UtcNow, ct,
            actorId: evt.ActorId, reason: evt.Reason);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Generated {Count} task(s) for dispatched lead {LeadId} → agent {AgentId} (tenant {TenantId}, {Source})",
            created, evt.LeadId, evt.AgentId, evt.TenantId, rules.Count == 0 ? "default first-contact task" : $"{rules.Count} rule(s)");
    }
}
