namespace Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class CreateTaskGenerationRuleHandler(LeadsDbContext db)
    : IRequestHandler<CreateTaskGenerationRuleCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(
        CreateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var rule = TaskGenerationRule.Create(
            tenantId:            cmd.TenantId,
            triggerEventType:    cmd.TriggerEventType,
            taskType:            cmd.TaskType,
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
