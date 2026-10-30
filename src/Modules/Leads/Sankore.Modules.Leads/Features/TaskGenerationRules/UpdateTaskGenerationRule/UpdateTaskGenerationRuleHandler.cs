namespace Sankore.Modules.Leads.Features.TaskGenerationRules.UpdateTaskGenerationRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Features.TaskTypes;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateTaskGenerationRuleHandler(LeadsDbContext db, TaskTypeCatalog taskTypes)
    : IRequestHandler<UpdateTaskGenerationRuleCommand, Result>
{
    public async Task<Result> Handle(UpdateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.TaskGenerationRules.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null)
            return Result.Fail("RULE_NOT_FOUND");

        // A rule keeps the type it already has even once that type is deactivated: deactivating a
        // type removes it from the choices, it must not make its rules uneditable.
        var taskType = cmd.TaskType?.Trim() ?? "";
        if (taskType != rule.TaskType)
        {
            var type = await taskTypes.CheckUsableAsync(taskType, ct);
            if (type.IsFailure) return Result.Fail(type.Error!);
        }

        rule.Update(cmd.TriggerEventType, taskType, cmd.Priority,
            cmd.TitleTemplate, cmd.SlaDuration, cmd.DueDuration, cmd.DescriptionTemplate);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
