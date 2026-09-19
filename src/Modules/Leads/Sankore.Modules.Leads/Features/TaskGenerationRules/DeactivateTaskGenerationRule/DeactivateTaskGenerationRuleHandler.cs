namespace Sankore.Modules.Leads.Features.TaskGenerationRules.DeactivateTaskGenerationRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateTaskGenerationRuleHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateTaskGenerationRuleCommand, Result>
{
    public async Task<Result> Handle(DeactivateTaskGenerationRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.TaskGenerationRules.AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null) return Result.Fail("RULE_NOT_FOUND");

        rule.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
