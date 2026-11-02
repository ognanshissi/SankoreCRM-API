using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Infrastructure.Branching;

/// <summary>
/// Writes what the record's module did with the action of a « Fin » step into the instance's
/// history, under that step — « Projet gagné par le circuit. », or why it was not. History only:
/// the circuit has already ended. A redelivered outcome finds its entry and adds nothing.
/// </summary>
public sealed class WorkflowEndActionOutcomeConsumer(WorkflowDbContext db) : IConsumer<WorkflowEndActionOutcome>
{
    public async Task Consume(ConsumeContext<WorkflowEndActionOutcome> context)
    {
        var outcome = context.Message;
        var ct = context.CancellationToken;

        // IgnoreQueryFilters: a consumer has no request, hence no ambient tenant.
        var step = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == outcome.TenantId && s.Id == outcome.InstanceStepId && s.InstanceId == outcome.InstanceId)
            .Select(s => new { s.StepDefinitionId })
            .FirstOrDefaultAsync(ct);
        if (step is null) return;

        var eventCode = outcome.Done ? EventCodes.EndActionDone : EventCodes.EndActionNotDone;
        var recorded = await db.WorkflowAuditEntries
            .IgnoreQueryFilters()
            .AnyAsync(a => a.InstanceId == outcome.InstanceId
                        && a.FromStateId == step.StepDefinitionId
                        && a.EventCode == eventCode
                        && a.Comment == outcome.Message, ct);
        if (recorded) return;

        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:      outcome.TenantId,
            instanceId:    outcome.InstanceId,
            fromStateId:   step.StepDefinitionId,
            toStateId:     step.StepDefinitionId,
            eventCode:     eventCode,
            actedByUserId: Guid.Empty,
            comment:       outcome.Message));

        await db.SaveChangesAsync(ct);
    }
}
