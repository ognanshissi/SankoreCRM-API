using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Infrastructure.Notifications;

/// <summary>
/// Writes what became of a « Notification » step into the instance's history — one entry per
/// audience (the person, the people working the record), pointing at the step's definition so the
/// instance screen shows it under the step. Before, the reason a message did not leave (no consent,
/// no SMS channel…) was only in a log.
///
/// <para>
/// History only: the step is already franchie and nothing here moves the instance. A redelivered
/// outcome finds its entry and adds nothing.
/// </para>
/// </summary>
public sealed class WorkflowNotificationOutcomeConsumer(
    WorkflowDbContext db,
    ILogger<WorkflowNotificationOutcomeConsumer> logger) : IConsumer<WorkflowNotificationOutcome>
{
    public async Task Consume(ConsumeContext<WorkflowNotificationOutcome> context)
    {
        var outcome = context.Message;
        var ct = context.CancellationToken;

        // IgnoreQueryFilters: a consumer has no request, hence no ambient tenant.
        var step = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == outcome.TenantId && s.Id == outcome.InstanceStepId && s.InstanceId == outcome.InstanceId)
            .Select(s => new { s.StepDefinitionId })
            .FirstOrDefaultAsync(ct);

        // A transition action « Notification » has no step to file under (SendNotificationExecutor).
        if (step is null)
        {
            logger.LogInformation(
                "Notification outcome for unknown step {StepId} of instance {InstanceId}: {Detail}",
                outcome.InstanceStepId, outcome.InstanceId, outcome.Detail);
            return;
        }

        var eventCode = outcome.Sent ? EventCodes.NotificationSent : EventCodes.NotificationNotSent;

        var recorded = await db.WorkflowAuditEntries
            .AnyAsync(a => a.InstanceId == outcome.InstanceId
                        && a.FromStateId == step.StepDefinitionId
                        && a.EventCode == eventCode
                        && a.Comment == outcome.Detail, ct);
        if (recorded) return;

        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId:      outcome.TenantId,
            instanceId:    outcome.InstanceId,
            fromStateId:   step.StepDefinitionId,
            toStateId:     step.StepDefinitionId,
            eventCode:     eventCode,
            actedByUserId: Guid.Empty,
            comment:       outcome.Detail));

        await db.SaveChangesAsync(ct);
    }
}
