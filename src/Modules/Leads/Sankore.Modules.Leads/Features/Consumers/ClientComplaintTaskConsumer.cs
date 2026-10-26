namespace Sankore.Modules.Leads.Features.Consumers;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// The task of a client complaint that needs somebody (2026-10-26): « Traiter la réclamation » for
/// its assignee when it is recorded, a late-deadline task for the agency manager afterwards. The
/// register is M01's; tasks are this module's, so the event crosses the boundary and nothing else.
///
/// <para>
/// Idempotent on (trigger, complaint): a redelivered event finds its task and adds nothing, while
/// a recording and each late deadline are distinct triggers and get a task each.
/// </para>
///
/// <para>
/// <b>The tasks close with the complaint</b> (<see cref="ClientComplaintProgressedEvent"/>): an
/// answer completes all of them, a closure without answer cancels them, and an acknowledgement
/// completes only the manager's « late acknowledgement » task — the assignee's still asks for the
/// answer. Closed silently, like the « Dispatch manuel » task an assignment closes: nobody did the
/// task by hand, so no « task completed » event follows.
/// </para>
/// </summary>
public sealed class ClientComplaintTaskConsumer(LeadsDbContext db, TimeProvider clock)
    : IConsumer<ClientComplaintActionRequiredEvent>, IConsumer<ClientComplaintProgressedEvent>
{
    internal static string TriggerOf(string reason) => $"ClientComplaint{reason}";

    public async Task Consume(ConsumeContext<ClientComplaintActionRequiredEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.TenantId == Guid.Empty || evt.RecipientUserId == Guid.Empty) return;

        var trigger = TriggerOf(evt.Reason);

        // IgnoreQueryFilters with an explicit tenant: a consumer has no HTTP request to take one from.
        if (await db.CrmTasks.IgnoreQueryFilters()
                .AnyAsync(t => t.TenantId == evt.TenantId && t.TriggerEventType == trigger && t.TriggerEventId == evt.ComplaintId, ct))
            return;

        var late = evt.Reason != ClientComplaintActionReasons.Filed;
        var title = evt.Reason switch
        {
            ClientComplaintActionReasons.AcknowledgementOverdue => $"Accusé de réception en retard : réclamation {evt.Reference}",
            ClientComplaintActionReasons.ResolutionOverdue => $"Réponse en retard : réclamation {evt.Reference}",
            _ => $"Traiter la réclamation {evt.Reference}",
        };

        var description = late
            ? $"« {evt.Subject} » — l'échéance du {evt.DueAt:dd/MM/yyyy} est dépassée. Voyez avec la personne "
              + "chargée de la réclamation, ou répondez au client depuis sa fiche."
            : $"« {evt.Subject} ». Accusez réception auprès du client avant le {evt.DueAt:dd/MM/yyyy}, puis "
              + "enregistrez la réponse depuis l'onglet Réclamations de sa fiche.";

        db.CrmTasks.Add(CrmTask.Create(
                tenantId: evt.TenantId,
                type: CrmTaskType.Generic,
                priority: late ? CrmTaskPriority.Critical : CrmTaskPriority.High,
                title: Truncate(title, CrmTask.TitleMaxLength),
                // A late task is due now: the deadline it reports is already behind it.
                dueAt: late ? clock.GetUtcNow() : evt.DueAt,
                assignedAgentId: evt.RecipientUserId,
                slaDeadline: late ? null : evt.DueAt,
                description: Truncate(description, CrmTask.DescriptionMaxLength),
                triggerEventType: trigger,
                triggerEventId: evt.ComplaintId)
            .AboutClient(evt.ClientId));

        await db.SaveChangesAsync(ct);
    }

    public async Task Consume(ConsumeContext<ClientComplaintProgressedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.TenantId == Guid.Empty) return;

        string[] triggers = evt.Stage == ClientComplaintStages.Acknowledged
            ? [TriggerOf(ClientComplaintActionReasons.AcknowledgementOverdue)]
            : [TriggerOf(ClientComplaintActionReasons.Filed),
               TriggerOf(ClientComplaintActionReasons.AcknowledgementOverdue),
               TriggerOf(ClientComplaintActionReasons.ResolutionOverdue)];

        // Open ones only, so a redelivery finds nothing left to close.
        var open = await db.CrmTasks
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(t => t.TenantId == evt.TenantId
                     && t.TriggerEventId == evt.ComplaintId
                     && triggers.Contains(t.TriggerEventType!)
                     && t.Status != CrmTaskStatus.Completed
                     && t.Status != CrmTaskStatus.Cancelled)
            .ToListAsync(ct);

        if (open.Count == 0) return;

        foreach (var task in open)
        {
            if (evt.Stage == ClientComplaintStages.Closed) task.Cancel();
            else task.Complete();
        }

        await db.SaveChangesAsync(ct);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
