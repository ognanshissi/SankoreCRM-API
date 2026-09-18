namespace Sankore.Modules.Leads.Features.DispatchingRules.DeactivateDispatchingRule;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class DeactivateDispatchingRuleHandler(LeadsDbContext db)
    : IRequestHandler<DeactivateDispatchingRuleCommand, Result>
{
    public async Task<Result> Handle(
        DeactivateDispatchingRuleCommand cmd, CancellationToken ct)
    {
        var rule = await db.DispatchingRules
            .AsTracking()
            .FirstOrDefaultAsync(r => r.Id == cmd.RuleId, ct);

        if (rule is null)
            return Result.Fail("RULE_NOT_FOUND");

        rule.Deactivate();
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
