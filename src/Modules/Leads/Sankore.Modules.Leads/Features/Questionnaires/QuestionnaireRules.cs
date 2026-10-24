namespace Sankore.Modules.Leads.Features.Questionnaires;

using System.Text.Json;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// What a template's rules make of a set of answers: which questions they hide, which they make
/// required, and the score. One body for the lead's qualification and the project's
/// questionnaires, so the same answers never score differently by route.
/// </summary>
internal static class QuestionnaireRules
{
    internal static HashSet<Guid> HiddenQuestionIds(
        IReadOnlyList<QualificationQuestion> questions,
        IReadOnlyDictionary<Guid, string> answerMap)
        => Triggered(questions, answerMap, QuestionRuleAction.Hide);

    internal static HashSet<Guid> ExtraRequiredQuestionIds(
        IReadOnlyList<QualificationQuestion> questions,
        IReadOnlyDictionary<Guid, string> answerMap)
        => Triggered(questions, answerMap, QuestionRuleAction.Require);

    /// <summary>The visible questions, required or made required, left unanswered.</summary>
    internal static IReadOnlyList<QualificationQuestion> MissingRequired(
        QualificationTemplate template, IReadOnlyDictionary<Guid, string> answerMap)
    {
        var hidden = HiddenQuestionIds(template.Questions, answerMap);
        var extra = ExtraRequiredQuestionIds(template.Questions, answerMap);

        return template.Questions
            .Where(q => !hidden.Contains(q.Id))
            .Where(q => (q.IsRequired || extra.Contains(q.Id))
                     && (!answerMap.TryGetValue(q.Id, out var v) || string.IsNullOrWhiteSpace(v)))
            .ToList();
    }

    private static HashSet<Guid> Triggered(
        IReadOnlyList<QualificationQuestion> questions,
        IReadOnlyDictionary<Guid, string> answerMap,
        QuestionRuleAction action)
    {
        var triggered = new HashSet<Guid>();
        foreach (var q in questions)
            foreach (var rule in q.GetRules())
            {
                if (rule.Action != action) continue;
                if (answerMap.TryGetValue(rule.TriggerQuestionId, out var v) &&
                    v.Equals(rule.TriggerValue, StringComparison.OrdinalIgnoreCase))
                {
                    triggered.Add(q.Id);
                    break;
                }
            }
        return triggered;
    }

    /// <summary>Weighted score over the visible questions, 0–100, with the factors as JSON.</summary>
    internal static (int Score, string FactorsJson) Score(
        QualificationTemplate template,
        IReadOnlyDictionary<Guid, string> answerMap,
        HashSet<Guid> hiddenIds)
    {
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
}
