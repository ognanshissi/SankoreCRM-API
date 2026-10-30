namespace Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateTaskGenerationRuleHandler(LeadsDbContext db, TaskTypeCatalog taskTypes)
    : IRequestHandler<CreateTaskGenerationRuleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var type = await taskTypes.CheckUsableAsync(cmd.TaskType, ct);
        if (type.IsFailure) return Result.Fail<Guid>(type.Error!);

        var rule = TaskGenerationRule.Create(
            tenantId:            cmd.TenantId,
            triggerEventType:    cmd.TriggerEventType,
            taskType:            cmd.TaskType.Trim(),
            priority:            cmd.Priority,
            titleTemplate:       cmd.TitleTemplate,
            slaDuration:         cmd.SlaDuration,
            dueDuration:         cmd.DueDuration,
            descriptionTemplate: cmd.DescriptionTemplate);

        db.TaskGenerationRules.Add(rule);
        await db.SaveChangesAsync(ct);

        return Result.Ok(rule.Id);
    }
}
