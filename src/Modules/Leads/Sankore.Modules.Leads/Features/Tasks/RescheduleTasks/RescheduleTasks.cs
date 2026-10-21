namespace Sankore.Modules.Leads.Features.Tasks.RescheduleTasks;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

public sealed record TaskDueDateInput(Guid TaskId, DateTimeOffset DueAt);

/// <summary>
/// Moves the due date of one or several tasks, all or nothing. One task is « reporter à demain »
/// on « Ma journée »; several is « Répartir sur la semaine », which spreads a day of first
/// contacts. A batch rather than one call per task: half a spread applied leaves the agent's day
/// in a state they never chose.
/// </summary>
internal sealed record RescheduleTasksCommand(IReadOnlyList<TaskDueDateInput> Items)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public const int MaxItems = 100;

    public string ResourceType => "CrmTask";
    public string? ResourceId => Items.Count == 1 ? Items[0].TaskId.ToString() : null;
}

internal sealed class RescheduleTasksHandler(
    LeadsDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<RescheduleTasksCommand, Result>
{
    public async Task<Result> Handle(RescheduleTasksCommand cmd, CancellationToken ct)
    {
        if (cmd.Items.Count == 0) return Result.Fail("NO_TASK");
        if (cmd.Items.Count > RescheduleTasksCommand.MaxItems) return Result.Fail("TOO_MANY_TASKS");

        var ids = cmd.Items.Select(i => i.TaskId).Distinct().ToList();
        if (ids.Count != cmd.Items.Count) return Result.Fail("DUPLICATE_TASK");

        var tasks = await db.CrmTasks.AsTracking()
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        if (tasks.Count != ids.Count) return Result.Fail("TASK_NOT_FOUND");

        var now = clock.GetUtcNow();
        foreach (var item in cmd.Items)
        {
            var task = tasks[item.TaskId];

            // Same guard as CompleteTask: the holder or a supervisor.
            if (task.AssignedAgentId is { } holder && holder != currentUser.Id && !IsSupervisor())
                return Result.Fail("NOT_TASK_OWNER");

            var result = task.Reschedule(item.DueAt, now);
            if (result.IsFailure) return result;
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private bool IsSupervisor() =>
        currentUser.Roles.Any(r =>
            r is "System" or "Administrator" or "SalesManager" or "BranchManager");
}
