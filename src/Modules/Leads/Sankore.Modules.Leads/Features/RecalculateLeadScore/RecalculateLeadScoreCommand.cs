namespace Sankore.Modules.Leads.Features.RecalculateLeadScore;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Recalculates a lead's score using the <see cref="QualifyLead.LeadScoreCalculator"/>
/// and persists the new value with a ScoreHistory entry.
/// Can be triggered manually (endpoint) or automatically (after activity logging).
/// </summary>
internal sealed record RecalculateLeadScoreCommand(
    Guid LeadId,
    string TriggerEvent = "MANUAL_RECALCULATION"
) : IRequest<Result<RecalculateLeadScoreResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

internal sealed record RecalculateLeadScoreResult(
    Guid LeadId,
    int PreviousScore,
    int NewScore,
    bool CriticalChange,
    string FactorsJson);
