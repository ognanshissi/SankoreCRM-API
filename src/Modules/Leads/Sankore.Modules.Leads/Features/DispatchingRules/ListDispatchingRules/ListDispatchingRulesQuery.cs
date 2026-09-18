namespace Sankore.Modules.Leads.Features.DispatchingRules.ListDispatchingRules;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListDispatchingRulesQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<DispatchingRuleDto>>>;
