namespace Sankore.Modules.Leads.Features.Tasks;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Workflow.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// The institution switched its KYC off (docs/kyc-par-institution.md): the open tasks about a KYC
/// file are cancelled — nobody can act on a file that is read-only now. Default choice of the plan,
/// to confirm in the recette. A workflow « Tâche manuelle » task tells its step, as
/// <see cref="ArchivedLeadTasks"/> does. Production's tasks and its « Mode test » twin's.
/// </summary>
public sealed class TenantKycDisabledTasksConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<TenantKycDisabledTasksConsumer> logger)
    : IConsumer<TenantKycSwitchedEvent>
{
    private static readonly CrmTaskStatus[] Open = [CrmTaskStatus.Pending, CrmTaskStatus.InProgress];

    public async Task Consume(ConsumeContext<TenantKycSwitchedEvent> context)
    {
        var evt = context.Message;
        if (evt.Enabled || evt.TenantId == Guid.Empty) return;

        Guid? twin;
        using (var probe = scopeFactory.CreateScope())
        {
            if (await probe.ServiceProvider.GetRequiredService<ITenantKyc>().IsInForceAsync(evt.TenantId, context.CancellationToken))
                return;
            twin = (await probe.ServiceProvider.GetRequiredService<ITenantStore>().GetAsync(evt.TenantId, context.CancellationToken))?.TestTenantId;
        }

        foreach (var tenantId in twin is { } t ? [evt.TenantId, t] : new[] { evt.TenantId })
        {
            using var bg = BackgroundJobContext.SetScope(tenantId, evt.ActorId, "SYSTEM");
            using var scope = scopeFactory.CreateScope();
            var cancelled = await CancelOpenAsync(
                scope.ServiceProvider.GetRequiredService<LeadsDbContext>(),
                scope.ServiceProvider.GetRequiredKeyedService<IEventPublisher>(nameof(LeadsDbContext)),
                tenantId, context.CancellationToken);
            if (cancelled > 0)
                logger.LogInformation("KYC switched off for tenant {TenantId}: {Count} KYC tasks cancelled.", tenantId, cancelled);
        }
    }

    internal static async Task<int> CancelOpenAsync(LeadsDbContext db, IEventPublisher? publisher, Guid tenantId, CancellationToken ct)
    {
        var tasks = await db.CrmTasks.IgnoreQueryFilters().AsTracking()
            .Where(t => t.TenantId == tenantId && t.EntityType == TaskEntityType.KycFile && Open.Contains(t.Status))
            .ToListAsync(ct);

        var cancelled = 0;
        foreach (var task in tasks)
        {
            if (task.Cancel().IsFailure) continue;
            cancelled++;
            if (publisher is not null && task.TriggerEventType == WorkflowTaskRequested.TriggerType && task.TriggerEventId is { } stepId)
                await publisher.PublishAsync(new WorkflowStepTaskClosed(task.TenantId, stepId, false, null, task.Title), ct);
        }

        await db.SaveChangesAsync(ct);
        return cancelled;
    }
}
