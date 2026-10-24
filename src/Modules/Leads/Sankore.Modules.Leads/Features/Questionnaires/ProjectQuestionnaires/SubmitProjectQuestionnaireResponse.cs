namespace Sankore.Modules.Leads.Features.Questionnaires.ProjectQuestionnaires;

using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Records the answers to one of the questionnaires a project's product asks — of its file, or of
/// one person (BNF-B-08).
/// </summary>
/// <param name="SubjectType">« Lead » for the file, « Principal », « HouseholdMember » or « CustomerPrincipal ».</param>
/// <param name="SubjectId">As listed by <c>GET opportunities/{id}/questionnaires</c>.</param>
/// <param name="RespondentId">
/// Who answered, when that is not the subject. REQUIRED for a minor and then constrained to the
/// principal or the spouse (BNF-B-08).
/// </param>
internal sealed record SubmitProjectQuestionnaireResponseCommand(
    Guid ProjectId,
    Guid TemplateId,
    string SubjectType,
    Guid SubjectId,
    IReadOnlyList<QuestionnaireAnswerInput> Answers,
    Guid? RespondentId = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => ProjectId.ToString();
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

public sealed record SubmitProjectQuestionnaireResponseRequest(
    Guid TemplateId,
    /// <summary>« Lead » (the file), « Principal », « HouseholdMember » or « CustomerPrincipal ».</summary>
    string SubjectType,
    Guid SubjectId,
    IReadOnlyList<QuestionnaireAnswerInput> Answers,
    /// <summary>Required for a minor; must be the principal or the spouse (BNF-B-08).</summary>
    Guid? RespondentId = null);

internal sealed class SubmitProjectQuestionnaireResponseValidator
    : AbstractValidator<SubmitProjectQuestionnaireResponseCommand>
{
    public SubmitProjectQuestionnaireResponseValidator()
    {
        RuleFor(c => c.ProjectId).NotEmpty();
        RuleFor(c => c.TemplateId).NotEmpty();
        RuleFor(c => c.SubjectType).NotEmpty();
        RuleFor(c => c.SubjectId).NotEmpty();

        RuleFor(c => c.Answers).NotEmpty()
            .WithMessage("At least one answer is required; an empty submission is not a declaration.");

        RuleForEach(c => c.Answers).ChildRules(a =>
        {
            a.RuleFor(x => x.QuestionId).NotEmpty();
            // No pattern and no length cap on the value: a free-text health answer is a sentence,
            // and the schema is the questionnaire's own business, not this validator's.
            a.RuleFor(x => x.Value).NotNull();
        });
    }
}

internal sealed class SubmitProjectQuestionnaireResponseHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    HealthConsentGuard consentGuard,
    QuestionnaireCompletenessChecker completeness,
    ExpectedResponsePlanner planner,
    ICurrentUser currentUser,
    TimeProvider clock,
    LeadVisibility? visibility = null)
    : IRequestHandler<SubmitProjectQuestionnaireResponseCommand, Result>
{
    internal const string NotApplicable = "QUESTIONNAIRE_TEMPLATE_NOT_APPLICABLE";
    internal const string PlanUnavailable = "QUESTIONNAIRE_PLAN_UNAVAILABLE";
    internal const string RequiredNotAnswered = "REQUIRED_QUESTIONS_NOT_ANSWERED";

    public async Task<Result> Handle(SubmitProjectQuestionnaireResponseCommand cmd, CancellationToken ct)
    {
        var projects = visibility?.Apply(db.Opportunities, db.Leads) ?? db.Opportunities;
        var project = await projects.FirstOrDefaultAsync(o => o.Id == cmd.ProjectId, ct);
        if (project is null)
            return Result.Fail(OpportunityErrors.NotFound);

        // A closed project's file is what was decided on: answering it afterwards would rewrite it.
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail(OpportunityErrors.AlreadyClosed);

        var lead = await completeness.LeadOfAsync(project, ct);
        if (lead is not null)
        {
            // Same perimeter as the household: a health declaration about somebody's children is
            // not data any colleague with project rights may write.
            var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
            if (access.IsFailure)
                return access;
        }

        if (!Enum.TryParse<QualificationSubjectType>(cmd.SubjectType, ignoreCase: true, out var subjectType)
            || !Enum.IsDefined(subjectType))
            return Result.Fail("INVALID_SUBJECT_TYPE");

        var template = await db.QualificationTemplates
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId && t.Status == TemplateStatus.Published, ct);
        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        // Only what the product asks, of somebody it asks it of. Without it a caller could file
        // answers for an arbitrary person or template, and the project would look complete while an
        // insured person had answered nothing.
        IReadOnlyList<ExpectedResponse> plan;
        try
        {
            plan = await planner.PlanAsync(project, lead, ct);
        }
        catch (ProductLineUnavailableException)
        {
            return Result.Fail(PlanUnavailable);
        }

        if (!plan.Any(e => e.TemplateId == template.Id && e.SubjectType == subjectType && e.SubjectId == cmd.SubjectId))
            return Result.Fail(NotApplicable);

        if (template.IsHealthQuestionnaire)
        {
            // Consent FIRST, before the answers are anywhere near the entity (BNF-B-09). Consents
            // live on the lead: a project with none cannot record a health declaration yet.
            if (lead is null)
                return Result.Fail(HealthConsentGuard.Missing);

            var consent = await consentGuard.EnsureConsentAsync(lead.Id, subjectType, cmd.SubjectId, ct);
            if (consent.IsFailure)
                return consent;
        }

        if (lead is not null)
        {
            var member = lead.HouseholdMembers.FirstOrDefault(m => m.Id == cmd.SubjectId);
            var age = member?.Identity.AgeAt(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

            var respondent = consentGuard.EnsureRespondentValid(
                lead, subjectType, cmd.SubjectId, cmd.RespondentId, age);
            if (respondent.IsFailure)
                return respondent;
        }

        var answerMap = cmd.Answers
            .GroupBy(a => a.QuestionId)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        if (QuestionnaireRules.MissingRequired(template, answerMap).Count > 0)
            return Result.Fail(RequiredNotAnswered);

        // The score is informational — it moves neither the lead's score nor the project's
        // probability — and zero when the template is excluded (always, for health: BNF-B-09).
        var score = template.ExcludeFromScoring
            ? 0
            : QuestionnaireRules.Score(
                template, answerMap, QuestionnaireRules.HiddenQuestionIds(template.Questions, answerMap)).Score;

        var answers = answerMap.Select(a => (a.Key, a.Value)).ToList();

        var existing = await db.QualificationResponses
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.OpportunityId == project.Id
                  && r.TemplateId == template.Id
                  && r.SubjectType == subjectType
                  && r.SubjectId == cmd.SubjectId,
                ct);

        if (existing is not null)
        {
            var answered = existing.Answer(answers, currentUser.Id, cmd.RespondentId, score, clock.GetUtcNow());
            if (answered.IsFailure)
                return answered;
        }
        else
        {
            // Created on the PROJECT even when an answer given on the lead exists: that one may
            // serve other projects, and correcting it here must not rewrite theirs.
            var response = QualificationResponse.Create(
                tenantId:             project.TenantId,
                leadId:               project.LeadId,
                templateId:           template.Id,
                answeredBy:           currentUser.Id,
                clock:                clock,
                answers:              answers,
                computedScore:        score,
                questionnaireVersion: template.Version,
                subjectType:          subjectType,
                subjectId:            cmd.SubjectId,
                isHealthData:         template.IsHealthQuestionnaire,
                opportunityId:        project.Id);

            if (cmd.RespondentId is not null)
                response.Answer(answers, currentUser.Id, cmd.RespondentId, score, clock.GetUtcNow());

            db.QualificationResponses.Add(response);
        }

        // Keeps the expectation in step: a submission can be the first thing that happens after a
        // questionnaire is published.
        await planner.SyncAsync(project, lead, ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
