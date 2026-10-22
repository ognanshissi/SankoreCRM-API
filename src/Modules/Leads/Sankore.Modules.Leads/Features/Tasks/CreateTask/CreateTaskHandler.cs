namespace Sankore.Modules.Leads.Features.Tasks.CreateTask;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Features.Tasks.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// A task created by a person — for themselves from « Ma journée », for a colleague, on a lead, a
/// client, a KYC file or nothing. The subject is checked (it exists and the creator may work on
/// it), the people are checked (users of this tenant), and the task, its criteria, tags and
/// associated colleagues are written together.
///
/// <para>
/// The holder is told by e-mail (<c>task.assigned</c>) and in the bell, the associated colleagues
/// in the bell — unless they are the creator: nobody is notified of what they just did.
/// </para>
/// </summary>
internal sealed class CreateTaskHandler(
    LeadsDbContext db,
    ICurrentUser me,
    TaskSubjects subjects,
    IAdministrationModule administration,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    TimeProvider clock)
    : IRequestHandler<CreateTaskCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateTaskCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var entityType = cmd.EntityType ?? (cmd.LeadId.HasValue ? TaskEntityType.Lead : null);
        var entityId = cmd.EntityType.HasValue ? cmd.EntityId : cmd.LeadId;

        if (entityType is { } type)
        {
            if (entityId is not { } id || id == Guid.Empty) return Result.Fail<Guid>("TASK_ENTITY_INCOMPLETE");
            var subject = await subjects.ValidateAsync(type, id, ct);
            if (subject.IsFailure) return Result.Fail<Guid>(subject.Error!);
            entityId = subject.Value;
        }
        else if (cmd.EntityId.HasValue)
        {
            return Result.Fail<Guid>("TASK_ENTITY_INCOMPLETE");
        }

        var participants = (cmd.ParticipantIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();
        var people = participants.Concat(cmd.AssignedAgentId is { } a ? [a] : []).Distinct().ToList();
        if (people.Count > 0)
        {
            var known = await administration.GetUserNamesAsync(me.TenantId, people, ct);
            if (people.Any(p => !known.ContainsKey(p))) return Result.Fail<Guid>("TASK_USER_NOT_FOUND");
        }

        var created = CrmTask.CreateManual(
            me.TenantId, me.Id, cmd.Type, cmd.Priority, cmd.Title, cmd.DueAt,
            entityType, entityId, cmd.AssignedAgentId, cmd.Description, now);
        if (created.IsFailure) return Result.Fail<Guid>(created.Error!);

        var task = created.Value!;
        db.CrmTasks.Add(task);

        // The holder is not ALSO associated: one person, one role on the task.
        foreach (var userId in participants.Where(p => p != task.AssignedAgentId))
            db.CrmTaskParticipants.Add(CrmTaskParticipant.Add(task.TenantId, task.Id, userId, me.Id, now));

        var position = 0;
        foreach (var label in (cmd.Criteria ?? []).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var criterion = CrmTaskCriterion.Create(task.TenantId, task.Id, label, position++, me.Id, now);
            if (criterion.IsFailure) return Result.Fail<Guid>(criterion.Error!);
            db.CrmTaskCriteria.Add(criterion.Value!);
        }

        foreach (var tag in (cmd.Tags ?? []).Select(CrmTaskTag.Normalize).OfType<string>().Distinct())
            db.CrmTaskTags.Add(CrmTaskTag.Add(task.TenantId, task.Id, tag, me.Id, now));

        if (task.AssignedAgentId is { } holder && holder != me.Id)
        {
            await publisher.PublishAsync(new TaskAssignedIntegrationEvent(task.Id, task.TenantId, holder, task.Title, task.DueAt, task.LeadId), ct);
            await publisher.PublishAsync(new CrmTaskActivityEvent(
                task.TenantId, task.Id, TaskActivityKind.Assigned, me.Id, [holder], now.ToUnixTimeMilliseconds().ToString()), ct);
        }

        var associated = participants.Where(p => p != me.Id && p != task.AssignedAgentId).ToList();
        if (associated.Count > 0)
            await publisher.PublishAsync(new CrmTaskActivityEvent(
                task.TenantId, task.Id, TaskActivityKind.Associated, me.Id, associated, now.ToUnixTimeMilliseconds().ToString()), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok(task.Id);
    }
}
