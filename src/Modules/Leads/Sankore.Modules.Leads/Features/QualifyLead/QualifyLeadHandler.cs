namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class QualifyLeadHandler(
    LeadsDbContext db,
    LeadScoreCalculator calculator,
    TimeProvider clock,
    Microsoft.Extensions.Options.IOptions<LeadModuleSettings> settings)
    : IRequestHandler<QualifyLeadCommand, Result<QualifyLeadResult>>
{
    public async Task<Result<QualifyLeadResult>> Handle(
        QualifyLeadCommand cmd, CancellationToken ct)
    {
        var lead = await db.Leads
            .AsTracking()
            .FirstOrDefaultAsync(l => l.Id == cmd.LeadId, ct);

        if (lead is null)
            return Result.Fail<QualifyLeadResult>("LEAD_NOT_FOUND");

        if (lead.IsStale(cmd.ExpectedUpdatedAt))
            return Result.Fail<QualifyLeadResult>(LeadConcurrency.ConflictError);

        string factorsJson;
        int score;
        double completeness = lead.QualificationCompleteness; // keep existing unless template used
        Guid? qualificationResponseId = null;

        // ── Path 1: template-driven scoring ──────────────────────────────────
        if (cmd.TemplateId.HasValue && cmd.Answers is not null)
        {
            var template = await db.QualificationTemplates
                .Include(t => t.Questions)
                .FirstOrDefaultAsync(
                    t => t.Id == cmd.TemplateId.Value && t.Status == TemplateStatus.Published, ct);

            if (template is null)
                return Result.Fail<QualifyLeadResult>("QUALIFICATION_TEMPLATE_NOT_FOUND");

            var answerMap     = cmd.Answers.ToDictionary(a => a.QuestionId, a => a.Value);
            var hiddenIds     = Questionnaires.QuestionnaireRules.HiddenQuestionIds(template.Questions, answerMap);
            var extraRequired = Questionnaires.QuestionnaireRules.ExtraRequiredQuestionIds(template.Questions, answerMap);

            // Validate all required/rule-forced questions are answered.
            var missingRequired = template.Questions
                .Where(q => !hiddenIds.Contains(q.Id))
                .Where(q => (q.IsRequired || extraRequired.Contains(q.Id)) && !answerMap.ContainsKey(q.Id))
                .ToList();

            if (missingRequired.Count > 0)
                return Result.Fail<QualifyLeadResult>("REQUIRED_QUESTIONS_NOT_ANSWERED");

            (score, factorsJson) = Questionnaires.QuestionnaireRules.Score(template, answerMap, hiddenIds);

            // Completeness = answered active questions / total active questions.
            var activeQuestions   = template.Questions.Where(q => !hiddenIds.Contains(q.Id)).ToList();
            var answeredActive    = activeQuestions.Count(q => answerMap.TryGetValue(q.Id, out var v) && !string.IsNullOrWhiteSpace(v));
            completeness          = activeQuestions.Count > 0 ? (double)answeredActive / activeQuestions.Count : 0.0;

            var answers = cmd.Answers.Select(a => (a.QuestionId, a.Value)).ToList();

            // Re-qualifying the same lead against the same template UPDATES its response, it does
            // not add a second one. The identity of a response is
            // (lead, template, subject type, subject id) and `ux_qualification_responses_subject`
            // enforces it, so a blind Add here raised a unique violation on the second
            // qualification — a 500 on an ordinary re-qualify. Same shape as
            // SubmitProjectQuestionnaireResponseHandler, which has always looked first.
            //
            // Nothing is lost by overwriting: the ScoreHistory row written below keeps each
            // qualification's score, trigger and factors, so the succession stays on the lead's
            // timeline even though only the latest ANSWERS are kept.
            var existing = await db.QualificationResponses
                .AsTracking()
                .FirstOrDefaultAsync(
                    r => r.LeadId == lead.Id
                      && r.TemplateId == template.Id
                      && r.SubjectType == QualificationSubjectType.Lead
                      && r.SubjectId == lead.Id,
                    ct);

            if (existing is not null)
            {
                var answered = existing.Answer(
                    answers, cmd.QualifiedBy, respondentId: null, score, clock.GetUtcNow());
                if (answered.IsFailure)
                    return Result.Fail<QualifyLeadResult>(answered.Error!);

                qualificationResponseId = existing.Id;
            }
            else
            {
                var response = QualificationResponse.Create(
                    tenantId:      lead.TenantId,
                    leadId:        lead.Id,
                    templateId:    template.Id,
                    answeredBy:    cmd.QualifiedBy,
                    clock:         clock,
                    answers:       answers,
                    computedScore: score);

                db.QualificationResponses.Add(response);
                qualificationResponseId = response.Id;
            }
        }
        // ── Path 2: explicit score override ──────────────────────────────────
        else if (cmd.Score.HasValue)
        {
            score       = cmd.Score.Value;
            factorsJson = "{}";
        }
        // ── Path 3: auto-calculate from lead attributes + activity history ──────
        else
        {
            (score, factorsJson) = await calculator.CalculateAsync(lead, ct);
        }

        var qualifyResult = lead.Qualify(score, settings.Value.QualificationThreshold);
        if (qualifyResult.IsFailure)
            return Result.Fail<QualifyLeadResult>(qualifyResult.Error!);

        // Derive intent level from score and update the lead.
        var intentLevel = DeriveIntentLevel(score);
        lead.UpdateIntentLevel(intentLevel);

        // Update qualification completeness when changed.
        lead.SetQualificationCompleteness(completeness);

        db.ScoreHistories.Add(ScoreHistory.Create(
            tenantId:                lead.TenantId,
            leadId:                  lead.Id,
            score:                   score,
            triggerEvent:            cmd.TriggerEvent,
            factorsJson:             factorsJson,
            qualificationResponseId: qualificationResponseId));

        await db.SaveChangesAsync(ct);

        var (nextAction, nextDetail) = DetermineNextAction(score, lead.Status);

        return Result.Ok(new QualifyLeadResult(
            LeadId:                  lead.Id,
            Score:                   score,
            Status:                  lead.Status.ToString(),
            IntentLevel:             intentLevel.ToString(),
            FactorsJson:             factorsJson,
            NextAction:              nextAction,
            NextActionDetail:        nextDetail,
            QualificationResponseId: qualificationResponseId));
    }

    // ── Intent & next-action helpers ───────────────────────────────────────

    private static LeadIntentLevel DeriveIntentLevel(int score) => score switch
    {
        >= 80 => LeadIntentLevel.Hot,
        >= 60 => LeadIntentLevel.Warm,
        >= 40 => LeadIntentLevel.Cold,
        _     => LeadIntentLevel.Unknown
    };

    private static (QualificationNextAction Action, string Detail) DetermineNextAction(
        int score, LeadStatus status)
        => score switch
        {
            >= 60 => (QualificationNextAction.DispatchToAgent,
                      "Lead qualifié — prêt à être dispatché à un agent commercial."),
            >= 40 => (QualificationNextAction.CollectMoreData,
                      "Score insuffisant pour le dispatch — compléter les informations manquantes."),
            _     => (QualificationNextAction.Disqualify,
                      "Score trop faible — envisager la disqualification du lead.")
        };
}
