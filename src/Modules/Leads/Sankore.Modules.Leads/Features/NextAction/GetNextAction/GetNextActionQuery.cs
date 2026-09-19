namespace Sankore.Modules.Leads.Features.NextAction.GetNextAction;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

internal sealed record GetNextActionQuery(Guid LeadId)
    : IRequest<Result<NextActionDto>>;

public sealed record NextActionDto(
    Guid LeadId,
    NextActionType ActionType,
    string Title,
    string Detail,
    /// <summary>High / Medium / Low</summary>
    string Urgency,
    DateTimeOffset SuggestedDueAt);
