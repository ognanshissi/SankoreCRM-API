namespace Sankore.Modules.Leads.Domain;

using System.Text.Json;
using Sankore.Shared.Kernel;

/// <summary>
/// Persisted record of an agent's answers to a <see cref="QualificationTemplate"/>
/// for a specific lead. The computed score is derived from weighted answers and
/// is the authoritative input to <see cref="ScoreHistory"/> when a template is used.
/// </summary>
public sealed class QualificationResponse : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid TemplateId { get; private set; }
    public Guid AnsweredBy { get; private set; }
    public DateTimeOffset AnsweredAt { get; private set; }
    public int ComputedScore { get; private set; }

    /// <summary>
    /// JSON-serialized list of <c>{ questionId, value }</c> objects.
    /// Stored as jsonb for auditability; accessed via <see cref="GetAnswers"/>.
    /// </summary>
    public string AnswersJson { get; private set; } = "[]";

    private QualificationResponse() { } // EF Core

    public static QualificationResponse Create(
        Guid tenantId,
        Guid leadId,
        Guid templateId,
        Guid answeredBy,
        TimeProvider clock,
        IReadOnlyList<(Guid QuestionId, string Value)> answers,
        int computedScore)
    {
        var payload = answers.Select(a => new { questionId = a.QuestionId, value = a.Value }).ToList();
        return new()
        {
            Id            = Guid.NewGuid(),
            TenantId      = tenantId,
            LeadId        = leadId,
            TemplateId    = templateId,
            AnsweredBy    = answeredBy,
            AnsweredAt    = clock.GetUtcNow(),
            ComputedScore = computedScore,
            AnswersJson   = JsonSerializer.Serialize(payload)
        };
    }

    /// <summary>Deserializes the stored answers for read-side use.</summary>
    public IReadOnlyList<(Guid QuestionId, string Value)> GetAnswers()
    {
        var raw = JsonSerializer.Deserialize<AnswerPayload[]>(AnswersJson) ?? [];
        return raw.Select(a => (a.questionId, a.value)).ToList();
    }

    // ── private JSON shape ────────────────────────────────────────────────
#pragma warning disable IDE1006
    private sealed record AnswerPayload(Guid questionId, string value);
#pragma warning restore IDE1006
}
