using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Infrastructure.Waits;

/// <summary>
/// The CRM task of a « Tâche manuelle » step was closed in Leads. Completed: the step is approved by
/// whoever did the task and the circuit goes on. Cancelled: the step stays open, approvable by hand,
/// and the history says so. A task closed after its step (approved by hand meanwhile) changes nothing.
/// </summary>
public sealed class WorkflowStepTaskClosedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowStepTaskClosedConsumer> logger) : IConsumer<WorkflowStepTaskClosed>
{
    public async Task Consume(ConsumeContext<WorkflowStepTaskClosed> context)
    {
        var closed = context.Message;
        var ct = context.CancellationToken;
        await using var scope = scopeFactory.CreateAsyncScope();

        if (closed.Completed)
        {
            var moved = await scope.ServiceProvider.GetRequiredService<InstanceRunner>().CompleteTaskStepAsync(
                closed.TenantId, closed.InstanceStepId, closed.ActedByUserId ?? Guid.Empty,
                $"Tâche « {closed.TaskTitle} » terminée.", ct);
            if (!moved)
                logger.LogInformation("Task of step {StepId} completed after its step closed: nothing to move.", closed.InstanceStepId);
            return;
        }

        await StepHistory.AddAsync(scope.ServiceProvider.GetRequiredService<WorkflowDbContext>(),
            closed.TenantId, closed.InstanceStepId, EventCodes.TaskCancelled,
            $"Tâche « {closed.TaskTitle} » annulée : l'étape attend une approbation manuelle.", logger, ct);
    }
}

/// <summary>A line another module adds under a step of an instance's history (task created…).</summary>
public sealed class WorkflowStepNoteConsumer(
    WorkflowDbContext db,
    ILogger<WorkflowStepNoteConsumer> logger) : IConsumer<WorkflowStepNote>
{
    public Task Consume(ConsumeContext<WorkflowStepNote> context)
        => StepHistory.AddAsync(db, context.Message.TenantId, context.Message.InstanceStepId,
            context.Message.Code, context.Message.Detail, logger, context.CancellationToken);
}

internal static class StepHistory
{
    /// <summary>Adds the line once: a redelivered message finds it and adds nothing.</summary>
    public static async Task AddAsync(
        WorkflowDbContext db, Guid tenantId, Guid instanceStepId, string code, string detail,
        ILogger logger, CancellationToken ct)
    {
        var step = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == tenantId && s.Id == instanceStepId)
            .Select(s => new { s.InstanceId, s.StepDefinitionId })
            .FirstOrDefaultAsync(ct);
        if (step is null)
        {
            logger.LogInformation("History line for unknown step {StepId}: {Detail}", instanceStepId, detail);
            return;
        }

        var recorded = await db.WorkflowAuditEntries.AnyAsync(a => a.InstanceId == step.InstanceId
            && a.FromStateId == step.StepDefinitionId && a.EventCode == code && a.Comment == detail, ct);
        if (recorded) return;

        db.WorkflowAuditEntries.Add(WorkflowAuditEntry.Create(
            tenantId, step.InstanceId, step.StepDefinitionId, step.StepDefinitionId, code, Guid.Empty, detail));
        await db.SaveChangesAsync(ct);
    }
}
