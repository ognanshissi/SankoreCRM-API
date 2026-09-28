namespace Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpdateSegmentRulesHandler(
    ICustomerSettings settings,
    ICurrentUser currentUser,
    ILogger<UpdateSegmentRulesHandler> logger)
    : IRequestHandler<UpdateSegmentRulesCommand, Result<UpdateSegmentRulesResult>>
{
    public async Task<Result<UpdateSegmentRulesResult>> Handle(
        UpdateSegmentRulesCommand request, CancellationToken ct)
    {
        var rules = request.Rules ?? [];

        // Re-serialize from the parsed objects rather than storing the raw request body:
        // that normalizes the order (ascending priority), drops unknown fields and
        // guarantees the setting always round-trips through SegmentRuleEvaluation.TryParse.
        var json = SegmentRuleEvaluation.Serialize(rules);

        var stored = await settings.SetAsync(
            currentUser.TenantId, CustomerSettingKeys.SegmentRulesJson, json, currentUser.Id, ct);

        if (stored.IsFailure)
            return Result.Fail<UpdateSegmentRulesResult>(stored.Error!);

        var notEvaluable = rules.Count(r => !r.IsEvaluable());

        if (notEvaluable > 0)
        {
            logger.LogInformation(
                "Tenant {TenantId} stored {Total} segmentation rule(s), {Parked} of which stay inactive "
                + "until M03/M04 expose outstanding data.",
                currentUser.TenantId, rules.Count, notEvaluable);
        }

        return Result.Ok(new UpdateSegmentRulesResult(rules.Count, notEvaluable));
    }
}
