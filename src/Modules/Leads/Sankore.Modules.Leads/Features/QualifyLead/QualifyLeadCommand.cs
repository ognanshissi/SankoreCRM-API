namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Scoring paths (evaluated in priority order):
/// 1. TemplateId + Answers provided → weighted template scoring, response persisted.
/// 2. Score provided → explicit override, no template response.
/// 3. Neither → auto-score from lead attributes via <see cref="LeadScoreCalculator"/>.
/// </summary>
internal sealed record QualifyLeadCommand(
    Guid LeadId,
    Guid QualifiedBy,
    int? Score = null,
    string TriggerEvent = "MANUAL_QUALIFICATION",
    Guid? TemplateId = null,
    IReadOnlyList<QualificationAnswerInput>? Answers = null
) : IRequest<Result<QualifyLeadResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

public sealed record QualificationAnswerInput(Guid QuestionId, string Value);

public sealed record QualifyLeadResult(
    Guid LeadId,
    int Score,
    string Status,
    string FactorsJson,
    QualificationNextAction NextAction,
    string NextActionDetail,
    Guid? QualificationResponseId = null);
