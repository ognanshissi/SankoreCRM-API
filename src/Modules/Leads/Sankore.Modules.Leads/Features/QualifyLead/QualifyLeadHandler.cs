namespace Sankore.Modules.Leads.Features.QualifyLead;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class QualifyLeadHandler(
    LeadsDbContext db,
    LeadScoreCalculator calculator,
    TimeProvider clock)
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
            var hiddenIds     = GetHiddenQuestionIds(template.Questions, answerMap);
            var extraRequired = GetExtraRequiredQuestionIds(template.Questions, answerMap);

            // Validate all required/rule-forced questions are answered.
            var missingRequired = template.Questions
                .Where(q => !hiddenIds.Contains(q.Id))
                .Where(q => (q.IsRequired || extraRequired.Contains(q.Id)) && !answerMap.ContainsKey(q.Id))
                .ToList();

            if (missingRequired.Count > 0)
                return Result.Fail<QualifyLeadResult>("REQUIRED_QUESTIONS_NOT_ANSWERED");

            (score, factorsJson) = ScoreFromTemplate(template, cmd.Answers, hiddenIds);

            // Completeness = answered active questions / total active questions.
            var activeQuestions   = template.Questions.Where(q => !hiddenIds.Contains(q.Id)).ToList();
            var answeredActive    = activeQuestions.Count(q => answerMap.TryGetValue(q.Id, out var v) && !string.IsNullOrWhiteSpace(v));
            completeness          = activeQuestions.Count > 0 ? (double)answeredActive / activeQuestions.Count : 0.0;

            var response = QualificationResponse.Create(
                tenantId:      lead.TenantId,
                leadId:        lead.Id,
                templateId:    template.Id,
                answeredBy:    cmd.QualifiedBy,
                clock:         clock,
                answers:       cmd.Answers.Select(a => (a.QuestionId, a.Value)).ToList(),
                computedScore: score);

            db.QualificationResponses.Add(response);
            qualificationResponseId = response.Id;
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

        var qualifyResult = lead.Qualify(score);
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

    // ── Rule evaluation ────────────────────────────────────────────────────

    private static HashSet<Guid> GetHiddenQuestionIds(
        IReadOnlyList<QualificationQuestion> questions,
        Dictionary<Guid, string> answerMap)
    {
        var hidden = new HashSet<Guid>();
        foreach (var q in questions)
            foreach (var rule in q.GetRules())
            {
                if (rule.Action != QuestionRuleAction.Hide) continue;
                if (answerMap.TryGetValue(rule.TriggerQuestionId, out var v) &&
                    v.Equals(rule.TriggerValue, StringComparison.OrdinalIgnoreCase))
                {
                    hidden.Add(q.Id);
                    break;
                }
            }
        return hidden;
    }

    private static HashSet<Guid> GetExtraRequiredQuestionIds(
        IReadOnlyList<QualificationQuestion> questions,
        Dictionary<Guid, string> answerMap)
    {
        var required = new HashSet<Guid>();
        foreach (var q in questions)
            foreach (var rule in q.GetRules())
            {
                if (rule.Action != QuestionRuleAction.Require) continue;
                if (answerMap.TryGetValue(rule.TriggerQuestionId, out var v) &&
                    v.Equals(rule.TriggerValue, StringComparison.OrdinalIgnoreCase))
                {
                    required.Add(q.Id);
                    break;
                }
            }
        return required;
    }

    // ── Scoring helpers ────────────────────────────────────────────────────

    private static (int Score, string FactorsJson) ScoreFromTemplate(
        QualificationTemplate template,
        IReadOnlyList<QualificationAnswerInput> answers,
        HashSet<Guid> hiddenIds)
    {
        var answerMap       = answers.ToDictionary(a => a.QuestionId, a => a.Value);
        var activeQuestions = template.Questions.Where(q => !hiddenIds.Contains(q.Id)).ToList();

        int earnedWeight = 0;
        int totalWeight  = activeQuestions.Sum(q => q.Weight);

        var factorDetails = activeQuestions.Select(q =>
        {
            answerMap.TryGetValue(q.Id, out var value);
            var earned = ComputeEarned(q, value);
            earnedWeight += earned;
            return new { questionId = q.Id, label = q.Label, weight = q.Weight, earned };
        }).ToList();

        var score = totalWeight > 0
            ? Math.Clamp((int)Math.Round(earnedWeight / (double)totalWeight * 100), 0, 100)
            : 0;

        var factorsJson = JsonSerializer.Serialize(new
        {
            template    = template.Name,
            totalWeight,
            earnedWeight,
            score,
            factors     = factorDetails
        });

        return (score, factorsJson);
    }

    private static int ComputeEarned(QualificationQuestion question, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return question.Type switch
        {
            QuestionType.YesNo =>
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ? question.Weight : 0,
            QuestionType.SingleChoice or QuestionType.MultiChoice => question.Weight,
            QuestionType.Numeric => ComputeNumericEarned(question, value),
            QuestionType.Text    => question.Weight,
            _                    => 0
        };
    }

    private static int ComputeNumericEarned(QualificationQuestion question, string value)
    {
        if (!decimal.TryParse(value, out var n)) return 0;

        if (question.MinValue.HasValue && question.MaxValue.HasValue &&
            question.MaxValue.Value > question.MinValue.Value)
        {
            var ratio = (n - question.MinValue.Value) /
                        (question.MaxValue.Value - question.MinValue.Value);
            ratio = Math.Clamp(ratio, 0m, 1m);
            return (int)Math.Round((double)ratio * question.Weight);
        }

        return n > 0 ? question.Weight : 0;
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
