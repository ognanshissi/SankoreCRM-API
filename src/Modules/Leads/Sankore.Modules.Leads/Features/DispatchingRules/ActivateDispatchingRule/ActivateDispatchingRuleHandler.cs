namespace Sankore.Modules.Leads.Features.DispatchingRules.ActivateDispatchingRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class ActivateDispatchingRuleHandler(LeadsDbContext db)
    : IRequestHandler<ActivateDispatchingRuleCommand, Result>
{
    public async Task<Result> Handle(
        ActivateDispatchingRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.DispatchingRules
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null)
            return Result.Fail("RULE_NOT_FOUND");

        rule.Activate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
