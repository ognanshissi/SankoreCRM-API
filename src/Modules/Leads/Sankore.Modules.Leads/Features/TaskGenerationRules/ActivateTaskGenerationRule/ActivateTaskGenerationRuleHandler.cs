namespace Sankore.Modules.Leads.Features.TaskGenerationRules.ActivateTaskGenerationRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateTaskGenerationRuleHandler(LeadsDbContext db)
    : IRequestHandler<ActivateTaskGenerationRuleCommand, Result>
{
    public async Task<Result> Handle(ActivateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.TaskGenerationRules.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null) return Result.Fail("RULE_NOT_FOUND");

        rule.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
