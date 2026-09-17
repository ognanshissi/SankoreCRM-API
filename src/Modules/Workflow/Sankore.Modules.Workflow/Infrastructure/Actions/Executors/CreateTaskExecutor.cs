using System.Text.Json;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure;

namespace Sankore.Modules.Workflow.Infrastructure.Actions.Executors;

/// <summary>
/// Creates a <see cref="WorkflowTask"/> when a transition fires.
/// Config shape: <c>{ "title": "...", "description": "...", "assignedRoleCode": "...", "priority": "Normal", "dueDays": 3 }</c>
/// </summary>
internal sealed class CreateTaskExecutor(WorkflowDbContext db) : IActionExecutor
{
    public ActionType ActionType => ActionType.CreateTask;

    public async Task ExecuteAsync(WorkflowAction action, WorkflowContext context, CancellationToken ct)
    {
        var cfg = JsonSerializer.Deserialize<CreateTaskConfig>(action.ConfigJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? new CreateTaskConfig();

        if (string.IsNullOrWhiteSpace(cfg.Title))
            return;

        var task = WorkflowTask.Create(
            tenantId:        context.TenantId,
            instanceId:      context.InstanceId,
            title:           cfg.Title,
            description:     cfg.Description,
            assignedRoleCode: cfg.AssignedRoleCode,
            priority:        cfg.Priority,
            dueDays:         cfg.DueDays);

        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync(ct);
    }

    private sealed class CreateTaskConfig
    {
        public string Title { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string? AssignedRoleCode { get; init; }
        public TaskPriority Priority { get; init; } = TaskPriority.Normal;
        public int? DueDays { get; init; }
    }
}
