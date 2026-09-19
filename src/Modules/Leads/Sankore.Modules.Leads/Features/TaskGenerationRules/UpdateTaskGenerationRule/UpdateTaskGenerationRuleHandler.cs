namespace Sankore.Modules.Leads.Features.TaskGenerationRules.UpdateTaskGenerationRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class UpdateTaskGenerationRuleHandler(LeadsDbContext db)
    : IRequestHandler<UpdateTaskGenerationRuleCommand, Result>
{
    public async Task<Result> Handle(UpdateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.TaskGenerationRules.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null)
            return Result.Fail("RULE_NOT_FOUND");

        rule.Update(cmd.TriggerEventType, cmd.TaskType, cmd.Priority,
            cmd.TitleTemplate, cmd.SlaDuration, cmd.DueDuration, cmd.DescriptionTemplate);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
