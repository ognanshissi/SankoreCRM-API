namespace Sankore.Modules.Leads.Features.DispatchingRules.GetDispatchingRule;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record GetDispatchingRuleQuery(Guid RuleId)
    : IRequest<Result<DispatchingRuleDto>>;
