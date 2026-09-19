namespace Sankore.Modules.Leads.Domain;

using System.Text.Json;

/// <summary>
/// A single question within a <see cref="QualificationTemplate"/>.
/// Weight defines how many points a positive answer contributes (normalised across all
/// active questions to a 0-100 score). Numeric questions support proportional scoring via
/// MinValue / MaxValue.
/// </summary>
public sealed class QualificationQuestion
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>Optional section this question belongs to.</summary>
    public Guid? SectionId { get; private set; }

    public string Label { get; private set; } = default!;

    /// <summary>Short guidance shown beneath the question label.</summary>
    public string? HelpText { get; private set; }

    /// <summary>Input placeholder for Text and Numeric questions.</summary>
    public string? PlaceholderText { get; private set; }

    public QuestionType Type { get; private set; }

    /// <summary>JSON array of allowed option labels for SingleChoice / MultiChoice questions.</summary>
    public string? OptionsJson { get; private set; }

    /// <summary>
    /// Contribution weight (positive integer). The scorer normalises all active-question
    /// weights to 0-100. A weight of 30 among total weights of 100 contributes up to 30 pts.
    /// </summary>
    public int Weight { get; private set; }

    public bool IsRequired { get; private set; }

    /// <summary>Display order within the template / section (1-based).</summary>
    public int Order { get; private set; }

    /// <summary>Minimum accepted value for <see cref="QuestionType.Numeric"/> questions.
    /// When both Min and Max are set the score is proportional: weight × (value − min) / (max − min).</summary>
    public decimal? MinValue { get; private set; }

    /// <summary>Maximum accepted value for <see cref="QuestionType.Numeric"/> questions.</summary>
    public decimal? MaxValue { get; private set; }

    /// <summary>
    /// JSON array of conditional rules: <c>[{ triggerQuestionId, triggerValue, action }]</c>.
    /// Accessed via <see cref="GetRules"/>.
    /// </summary>
    public string? RulesJson { get; private set; }

    private QualificationQuestion() { } // EF Core

    public static QualificationQuestion Create(
        Guid templateId,
        string label,
        QuestionType type,
        int weight,
        bool isRequired,
        int order,
        Guid? sectionId = null,
        string[]? options = null,
        string? helpText = null,
        string? placeholderText = null,
        decimal? minValue = null,
        decimal? maxValue = null,
        IReadOnlyList<(Guid TriggerQuestionId, string TriggerValue, QuestionRuleAction Action)>? rules = null)
        => new()
        {
            Id              = Guid.NewGuid(),
            TemplateId      = templateId,
            SectionId       = sectionId,
            Label           = label.Trim(),
            HelpText        = helpText?.Trim(),
            PlaceholderText = placeholderText?.Trim(),
            Type            = type,
            Weight          = weight,
            IsRequired      = isRequired,
            Order           = order,
            MinValue        = minValue,
            MaxValue        = maxValue,
            OptionsJson     = options is { Length: > 0 }
                ? JsonSerializer.Serialize(options)
                : null,
            RulesJson       = rules is { Count: > 0 }
                ? JsonSerializer.Serialize(rules.Select(r => new
                  {
                      triggerQuestionId = r.TriggerQuestionId,
                      triggerValue      = r.TriggerValue,
                      action            = r.Action.ToString()
                  }))
                : null
        };

    /// <summary>Returns the deserialized option labels, or an empty array for non-choice questions.</summary>
    public string[] GetOptions()
        => OptionsJson is null
            ? []
            : JsonSerializer.Deserialize<string[]>(OptionsJson) ?? [];

    /// <summary>Returns the deserialized conditional rules.</summary>
    public IReadOnlyList<(Guid TriggerQuestionId, string TriggerValue, QuestionRuleAction Action)> GetRules()
    {
        if (RulesJson is null) return [];
        var raw = JsonSerializer.Deserialize<RulePayload[]>(RulesJson) ?? [];
        return raw
            .Select(r => (r.triggerQuestionId, r.triggerValue,
                Enum.Parse<QuestionRuleAction>(r.action, ignoreCase: true)))
            .ToList();
    }

#pragma warning disable IDE1006
    private sealed record RulePayload(Guid triggerQuestionId, string triggerValue, string action);
#pragma warning restore IDE1006
}
