namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead.Events;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Features.Tasks;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// A lead captured with an owner — typed in by its agent, usually — is never dispatched, and the
/// « Premier contact » task is born of a dispatch: nothing reminded the agent of it in « Ma journée ».
/// It gets the same task, due at the delay the dispatching rule would have given (same rule, same
/// temperature), and a later dispatch transfers it like any other. Out of band, for the reason
/// <see cref="LeadAutoDispatchConsumer"/> is: an import may carry hundreds of rows.
/// </summary>
public sealed class OwnedLeadFirstContactConsumer(IServiceScopeFactory scopeFactory) : IConsumer<LeadCapturedEvent>
{
    public async Task Consume(ConsumeContext<LeadCapturedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (!evt.HasExplicitOwner || evt.TenantId == Guid.Empty) return;

        // Before any scoped service: ITenantContext is built from it, and a consumer has no request.
        using var bgCtx = BackgroundJobContext.SetScope(evt.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LeadsDbContext>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == evt.LeadId, ct);
        // Assigned since: the dispatch gave the task. Contacted already: nothing left to remind.
        if (lead?.OwnerId is not { } owner || lead.CurrentAssignmentId is not null || lead.Status != LeadStatus.New)
            return;

        var rule = await new DispatchingRuleResolver(db).ResolveAsync(lead, null, ct);
        var rules = await db.TaskGenerationRules
            .Where(r => r.TriggerEventType == TaskTriggerEvents.LeadDispatched && r.IsActive)
            .ToListAsync(ct);

        await AssignmentTasks.SyncOnAssignedAsync(
            db, evt.TenantId, lead.Id, owner, now.Add(rule.FirstContactSlaFor(lead.IntentLevel)), rules, now, ct);
        await db.SaveChangesAsync(ct);
    }
}
