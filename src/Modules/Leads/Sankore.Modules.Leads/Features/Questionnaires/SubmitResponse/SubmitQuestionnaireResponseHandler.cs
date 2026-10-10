namespace Sankore.Modules.Leads.Features.Questionnaires.SubmitResponse;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class SubmitQuestionnaireResponseHandler(
    LeadsDbContext db,
    LeadAccessGuard accessGuard,
    HealthConsentGuard consentGuard,
    ExpectedResponsePlanner planner,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<SubmitQuestionnaireResponseCommand, Result>
{
    public async Task<Result> Handle(SubmitQuestionnaireResponseCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail(LeadConcurrency.ConflictError);

        // Same perimeter as the household: a health declaration about somebody's children is not
        // data any colleague with lead:read may write.
        var access = await accessGuard.EnsureCanWorkAsync(lead, ct);
        if (access.IsFailure)
            return access;

        if (!Enum.TryParse<QualificationSubjectType>(cmd.SubjectType, ignoreCase: true, out var subjectType)
            || !Enum.IsDefined(subjectType))
            return Result.Fail("INVALID_SUBJECT_TYPE");

        var template = await db.QualificationTemplates
            .Include(t => t.Questions)
            .FirstOrDefaultAsync(t => t.Id == cmd.TemplateId, ct);

        if (template is null)
            return Result.Fail("TEMPLATE_NOT_FOUND");

        // The subject must be somebody this lead actually has. Without it, a caller could create a
        // response for an arbitrary guid and the completeness check would never see it — the lead
        // would look complete while an insured person had answered nothing.
        var subjectExists = subjectType switch
        {
            QualificationSubjectType.Principal       => cmd.SubjectId == lead.Id,
            QualificationSubjectType.Lead            => cmd.SubjectId == lead.Id,
            QualificationSubjectType.HouseholdMember =>
                lead.ActiveHouseholdMembers.Any(m => m.Id == cmd.SubjectId),
            _ => false,
        };

        if (!subjectExists)
            return Result.Fail("QUALIFICATION_SUBJECT_NOT_FOUND");

        if (template.IsHealthQuestionnaire)
        {
            // Consent FIRST, before the answers are anywhere near the entity (BNF-B-09).
            var consent = await consentGuard.EnsureConsentAsync(
                lead.Id, subjectType, cmd.SubjectId, ct);
            if (consent.IsFailure)
                return consent;
        }

        var member = lead.HouseholdMembers.FirstOrDefault(m => m.Id == cmd.SubjectId);
        var age = member?.Identity.AgeAt(DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        var respondent = consentGuard.EnsureRespondentValid(
            lead, subjectType, cmd.SubjectId, cmd.RespondentId, age);
        if (respondent.IsFailure)
            return respondent;

        var answers = cmd.Answers.Select(a => (a.QuestionId, a.Value)).ToList();

        // A per-person questionnaire does NOT move the lead's F13.6 score in this lot. A health
        // one may not by law (BNF-B-09); a non-health one has no defined aggregation across
        // several people, and inventing one would be inventing a scoring rule. The stored score is
        // informational, and zero when the template is excluded.
        var score = template.ExcludeFromScoring ? 0 : ScoreAnswers(template, answers);

        var existing = await db.QualificationResponses
            .AsTracking()
            .FirstOrDefaultAsync(
                r => r.LeadId == lead.Id
                  && r.TemplateId == cmd.TemplateId
                  && r.SubjectType == subjectType
                  && r.SubjectId == cmd.SubjectId,
                ct);

        if (existing is not null)
        {
            var answered = existing.Answer(
                answers, currentUser.Id, cmd.RespondentId, score, clock.GetUtcNow());
            if (answered.IsFailure)
                return answered;
        }
        else
        {
            // No expected row — the questionnaire was published after the household was recorded,
            // or the lead has no product line and nothing planned it. Created answered, so an
            // agent is never blocked by the absence of a row the system should have made.
            db.QualificationResponses.Add(QualificationResponse.Create(
                tenantId:             lead.TenantId,
                leadId:               lead.Id,
                templateId:           cmd.TemplateId,
                answeredBy:           currentUser.Id,
                clock:                clock,
                answers:              answers,
                computedScore:        score,
                questionnaireVersion: template.Version,
                subjectType:          subjectType,
                subjectId:            cmd.SubjectId,
                isHealthData:         template.IsHealthQuestionnaire));
        }

        // Keeps the expectation in step: a submission can be the first thing that happens after a
        // questionnaire is published, and the plan is cheap when nothing is missing.
        await planner.SyncAsync(lead, ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    /// <summary>
    /// Weighted sum over the answered questions, clamped to 0-100 — the same shape
    /// <c>QualifyLeadHandler</c> uses for a lead-scoped template.
    /// </summary>
    private static int ScoreAnswers(
        QualificationTemplate template, IReadOnlyList<(Guid QuestionId, string Value)> answers)
    {
        var weights = template.Questions.ToDictionary(q => q.Id, q => q.Weight);

        var earned = answers
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .Sum(a => weights.TryGetValue(a.QuestionId, out var w) ? w : 0);

        var total = weights.Values.Sum();

        return total <= 0 ? 0 : Math.Clamp((int)Math.Round(100.0 * earned / total), 0, 100);
    }
}
