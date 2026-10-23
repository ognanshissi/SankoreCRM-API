namespace Sankore.Modules.Leads.Features.GetScoreHistory;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

internal sealed record GetScoreHistoryQuery(Guid LeadId) : ILeadReadRequest, IRequest<Result<IReadOnlyList<ScoreHistoryDto>>>;

public sealed record ScoreHistoryDto(
    Guid Id,
    int Score,
    string TriggerEvent,
    string FactorsJson,
    DateTimeOffset RecalculatedAt);
