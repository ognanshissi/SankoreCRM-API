namespace Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Replaces the tenant's whole segmentation rule set (US-M01-BE-27).
///
/// Set-based on purpose: a rule set is an ORDERED decision list, so patching one rule in
/// isolation cannot be reasoned about — the caller always sends the list it wants to be true.
///
/// Audited (<see cref="ICommand"/>): changing these rules silently moves clients between
/// commercial segments the next night, which is a business decision worth a trail.
/// </summary>
public sealed record UpdateSegmentRulesCommand(IReadOnlyList<SegmentRuleDefinition> Rules)
    : IRequest<Result<UpdateSegmentRulesResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ClientSegmentRules";
    public string? ResourceId => null;
}

/// <param name="RuleCount">Rules stored.</param>
/// <param name="NotEvaluableCount">Among them, rules parked until M03/M04 expose outstanding data.</param>
public sealed record UpdateSegmentRulesResult(int RuleCount, int NotEvaluableCount);
