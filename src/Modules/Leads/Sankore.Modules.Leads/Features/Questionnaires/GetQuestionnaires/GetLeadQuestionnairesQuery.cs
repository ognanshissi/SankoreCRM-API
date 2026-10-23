namespace Sankore.Modules.Leads.Features.Questionnaires.GetQuestionnaires;

using MediatR;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Shared.Kernel;

/// <summary>
/// Every questionnaire a lead's people owe or have answered (BNF-B-08), plus what is still missing.
/// </summary>
/// <param name="IncludeHealthAnswers">
/// Whether the caller holds <c>lead:health:read</c>. Decided by the ENDPOINT from the caller's
/// claims — see <c>HealthAnswerAccess</c>.
/// </param>
internal sealed record GetLeadQuestionnairesQuery(Guid LeadId, bool IncludeHealthAnswers)
    : ILeadReadRequest, IRequest<Result<LeadQuestionnairesDto>>;

/// <param name="Answers">
/// Null when the caller may not read them — which is every health response unless they hold
/// <c>lead:health:read</c>, and every response that has been purged. The STATUS is always visible:
/// an agent chasing a missing declaration needs to know it is missing without reading it.
/// </param>
/// <param name="AnswersWithheldReason">
/// Why <paramref name="Answers"/> is null — <c>HEALTH_ANSWERS_FORBIDDEN</c> or
/// <c>QUALIFICATION_RESPONSE_PURGED</c>. Without it the screen cannot tell "you may not see this"
/// from "there is nothing to see".
/// </param>
public sealed record QuestionnaireResponseDto(
    Guid ResponseId,
    Guid TemplateId,
    string TemplateName,
    bool IsHealthQuestionnaire,
    string SubjectType,
    Guid SubjectId,
    string SubjectLabel,
    int QuestionnaireVersion,
    string Status,
    Guid? RespondentId,
    DateTimeOffset? AnsweredAt,
    bool IsPurged,
    IReadOnlyList<HealthAnswer>? Answers,
    string? AnswersWithheldReason);

public sealed record LeadQuestionnairesDto(
    Guid LeadId,
    IReadOnlyList<QuestionnaireResponseDto> Responses,
    IReadOnlyList<MissingQuestionnaire> Missing,
    /// <summary>
    /// False when a required questionnaire is still unanswered — the same condition that refuses
    /// the conversion with <c>MISSING_QUESTIONNAIRE_RESPONSES</c>.
    /// </summary>
    bool IsComplete);
