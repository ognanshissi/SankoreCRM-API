namespace Sankore.Modules.Leads.Features.ScoringConfigs.ListScoringConfigs;

using MediatR;
using Sankore.Shared.Kernel;

internal sealed record ListScoringConfigsQuery(bool? ActiveOnly = null)
    : IRequest<Result<IReadOnlyList<ScoringConfigDto>>>;
