namespace Sankore.Modules.Leads.Features.Questionnaires.SubmitResponse;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records one person's answers to one questionnaire (BNF-B-08).
/// </summary>
/// <param name="SubjectType">"Principal" | "HouseholdMember".</param>
/// <param name="SubjectId">The lead's id for the principal, the member's id otherwise.</param>
/// <param name="RespondentId">
/// Who answered, when that is not the subject. REQUIRED for a minor and then constrained to the
/// principal or the spouse (BNF-B-08).
/// </param>
internal sealed record SubmitQuestionnaireResponseCommand(
    Guid LeadId,
    Guid TemplateId,
    string SubjectType,
    Guid SubjectId,
    IReadOnlyList<QuestionnaireAnswerInput> Answers,
    Guid? RespondentId = null,
    DateTimeOffset? ExpectedUpdatedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Lead";
    public string? ResourceId => LeadId.ToString();
}

/// <summary>
/// One answer.
/// </summary>
/// <remarks>
/// The VALUE travels here and nowhere else. <c>AuditBehavior</c> records a command's resource and
/// actor, not its payload — which is what BNF-B-09 means by "l'audit ne trace que l'identifiant de
/// la question". Do not add this type to anything that logs itself.
/// </remarks>
public sealed record QuestionnaireAnswerInput(Guid QuestionId, string Value);
