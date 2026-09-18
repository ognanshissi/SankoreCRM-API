namespace Sankore.Modules.Leads.Domain;

using System.Text.Json;

/// <summary>
/// A single question within a <see cref="QualificationTemplate"/>.
/// Weight defines how many points a positive answer contributes to the lead score
/// (relative to the sum of all question weights, then normalized to 0-100).
/// </summary>
public sealed class QualificationQuestion
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }
    public string Label { get; private set; } = default!;
    public QuestionType Type { get; private set; }

    /// <summary>JSON array of allowed option labels for SingleChoice / MultiChoice questions.</summary>
    public string? OptionsJson { get; private set; }

    /// <summary>
    /// Contribution weight (any positive integer). The scorer normalizes all weights to 0-100.
    /// A weight of 30 among total weights of 100 contributes up to 30 pts.
    /// </summary>
    public int Weight { get; private set; }

    public bool IsRequired { get; private set; }

    /// <summary>Display order within the template (1-based).</summary>
    public int Order { get; private set; }

    private QualificationQuestion() { } // EF Core

    public static QualificationQuestion Create(
        Guid templateId,
        string label,
        QuestionType type,
        int weight,
        bool isRequired,
        int order,
        string[]? options = null)
        => new()
        {
            Id         = Guid.NewGuid(),
            TemplateId = templateId,
            Label      = label.Trim(),
            Type       = type,
            Weight     = weight,
            IsRequired = isRequired,
            Order      = order,
            OptionsJson = options is { Length: > 0 }
                ? JsonSerializer.Serialize(options)
                : null
        };

    /// <summary>Returns the deserialized option labels, or an empty array for non-choice questions.</summary>
    public string[] GetOptions()
        => OptionsJson is null
            ? []
            : JsonSerializer.Deserialize<string[]>(OptionsJson) ?? [];
}
