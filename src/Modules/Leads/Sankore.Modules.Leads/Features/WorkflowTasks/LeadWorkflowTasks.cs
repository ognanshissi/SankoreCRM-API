using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;

namespace Sankore.Modules.Leads.Features.WorkflowTasks;

/// <summary>
/// The CRM tasks of workflow « Tâche manuelle » steps: one per open step, about the circuit's
/// record, in the « Ma journée » of the agent working that record. Completing it moves the circuit
/// on (<see cref="WorkflowStepTaskClosed"/>, published by the complete/cancel handlers).
/// </summary>
public sealed class LeadWorkflowTasks(
    LeadsDbContext db,
    ICustomersModule customers,
    IAdministrationModule administration,
    TimeProvider clock)
{
    /// <summary>Default due date when the step has no delay.</summary>
    public static readonly TimeSpan DefaultDue = TimeSpan.FromHours(24);

    /// <summary>The history line for the step, or null when the task already existed.</summary>
    public async Task<string?> CreateAsync(WorkflowTaskRequested request, CancellationToken ct)
    {
        // Idempotent on the step: a redelivered request finds its task.
        var exists = await db.CrmTasks
            .IgnoreQueryFilters()
            .AnyAsync(t => t.TenantId == request.TenantId
                        && t.TriggerEventType == WorkflowTaskRequested.TriggerType
                        && t.TriggerEventId == request.InstanceStepId, ct);
        if (exists) return null;

        var (entityType, agentId) = await RecordAsync(request, ct);

        var now = clock.GetUtcNow();
        var task = CrmTask.ForWorkflowStep(
            request.TenantId,
            request.StepName,
            $"Étape « {request.StepName} » d'un parcours : terminez cette tâche pour faire avancer le dossier.",
            entityType,
            entityType is null ? null : request.EntityId,
            agentId,
            now + (request.DueInHours is > 0 ? TimeSpan.FromHours(request.DueInHours.Value) : DefaultDue),
            request.InstanceStepId,
            WorkflowTaskRequested.TriggerType);

        db.CrmTasks.Add(task);
        await db.SaveChangesAsync(ct);

        if (agentId is not { } agent)
            return $"Tâche « {request.StepName} » créée sans agent : à attribuer dans les tâches.";

        var name = (await administration.GetUserNamesAsync(request.TenantId, [agent], ct)).GetValueOrDefault(agent);
        return name is null
            ? $"Tâche « {request.StepName} » créée pour l'agent du dossier."
            : $"Tâche « {request.StepName} » créée pour {name}.";
    }

    /// <summary>Cancels the step's task if it is still open. True when one was.</summary>
    public async Task<bool> WithdrawAsync(WorkflowTaskWithdrawn withdrawn, CancellationToken ct)
    {
        var task = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TenantId == withdrawn.TenantId
                                   && t.TriggerEventType == WorkflowTaskRequested.TriggerType
                                   && t.TriggerEventId == withdrawn.InstanceStepId, ct);
        if (task is null || task.IsClosed) return false;

        task.Cancel();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// What the task is about and who gets it: the record's own agent — the lead's assignee, else
    /// its owner; the project's owner, else its lead's agent; the client's advisor. A KYC file has
    /// none: the task is created unassigned and the managers see it in the task list.
    /// </summary>
    private async Task<(TaskEntityType? Type, Guid? Agent)> RecordAsync(WorkflowTaskRequested request, CancellationToken ct)
    {
        switch (request.EntityType.ToLowerInvariant())
        {
            case "lead":
            {
                var lead = await db.Leads.IgnoreQueryFilters()
                    .Where(l => l.TenantId == request.TenantId && l.Id == request.EntityId)
                    .Select(l => new { l.CurrentAssignedId, l.OwnerId })
                    .FirstOrDefaultAsync(ct);
                return (TaskEntityType.Lead, lead?.CurrentAssignedId ?? lead?.OwnerId);
            }
            case "opportunity":
            {
                var project = await db.Opportunities.IgnoreQueryFilters()
                    .Where(o => o.TenantId == request.TenantId && o.Id == request.EntityId)
                    .Select(o => new { o.OwnerId, o.LeadId })
                    .FirstOrDefaultAsync(ct);
                var leadAgent = project?.LeadId is { } leadId
                    ? await db.Leads.IgnoreQueryFilters()
                        .Where(l => l.TenantId == request.TenantId && l.Id == leadId)
                        .Select(l => l.CurrentAssignedId ?? l.OwnerId)
                        .FirstOrDefaultAsync(ct)
                    : null;
                return (TaskEntityType.Project, project?.OwnerId ?? leadAgent);
            }
            case "account":
            {
                var client = await customers.GetClientSummaryAsync(request.TenantId, request.EntityId, ct);
                return (TaskEntityType.Client, client?.AdvisorUserId);
            }
            case "kycfile":
                return (TaskEntityType.KycFile, null);
            default:
                return (null, null);
        }
    }
}

public sealed class WorkflowTaskRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowTaskRequestedConsumer> logger) : IConsumer<WorkflowTaskRequested>
{
    public async Task Consume(ConsumeContext<WorkflowTaskRequested> context)
    {
        var request = context.Message;
        using var bgCtx = BackgroundJobContext.SetScope(request.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var note = await scope.ServiceProvider.GetRequiredService<LeadWorkflowTasks>().CreateAsync(request, context.CancellationToken);
        if (note is null) return;

        logger.LogInformation("Workflow step {StepId}: {Note}", request.InstanceStepId, note);
        await context.Publish(new WorkflowStepNote(request.TenantId, request.InstanceStepId, "TASK_CREATED", note),
            context.CancellationToken);
    }
}

public sealed class WorkflowTaskWithdrawnConsumer(IServiceScopeFactory scopeFactory) : IConsumer<WorkflowTaskWithdrawn>
{
    public async Task Consume(ConsumeContext<WorkflowTaskWithdrawn> context)
    {
        using var bgCtx = BackgroundJobContext.SetScope(context.Message.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LeadWorkflowTasks>().WithdrawAsync(context.Message, context.CancellationToken);
    }
}
