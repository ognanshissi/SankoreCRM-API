namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// An ordered group of questions within a <see cref="QualificationTemplate"/>.
/// Sections are purely organisational — they do not affect scoring.
/// </summary>
public sealed class QualificationSection
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }
    public string Title { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>Display order within the template (1-based).</summary>
    public int Order { get; private set; }

    private QualificationSection() { } // EF Core

    public static QualificationSection Create(
        Guid templateId,
        string title,
        string? description,
        int order)
        => new()
        {
            Id          = Guid.NewGuid(),
            TemplateId  = templateId,
            Title       = title.Trim(),
            Description = description?.Trim(),
            Order       = order
        };
}
