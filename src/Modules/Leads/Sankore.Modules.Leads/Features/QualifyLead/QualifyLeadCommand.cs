namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Qualifies a lead with an explicit score OR requests auto-scoring from the
/// <see cref="LeadScoreCalculator"/>. A ScoreHistory row is always persisted.
/// </summary>
internal sealed record QualifyLeadCommand(
    Guid LeadId,
    /// <summary>Explicit score 0-100. If null, the calculator derives it automatically.</summary>
    int? Score,
    string TriggerEvent = "MANUAL_QUALIFICATION"
) : IRequest<Result<QualifyLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record QualifyLeadResult(
    Guid LeadId,
    int Score,
    string Status,
    string FactorsJson);
