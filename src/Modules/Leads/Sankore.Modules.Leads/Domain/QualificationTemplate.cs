namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-configurable qualification questionnaire scoped to a financial product.
/// Lifecycle: Draft → Published → Archived.
/// Only one <see cref="TemplateStatus.Published"/> template can exist per
/// <see cref="ProductType"/> per tenant at any time.
/// </summary>
public sealed class QualificationTemplate : AggregateRoot
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>Financial product this template is scoped to. Null = generic (not product-specific).</summary>
    public ProductType? ProductType { get; private set; }

    public TemplateStatus Status { get; private set; }

    /// <summary>Monotonically increasing counter — incremented each time the template is published.</summary>
    public int Version { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<QualificationSection> _sections = [];
    public IReadOnlyList<QualificationSection> Sections => _sections.AsReadOnly();

    private readonly List<QualificationQuestion> _questions = [];
    public IReadOnlyList<QualificationQuestion> Questions => _questions.AsReadOnly();

    private QualificationTemplate() { } // EF Core

    public static QualificationTemplate Create(
        Guid tenantId,
        string name,
        DateTimeOffset now,
        string? description = null,
        ProductType? productType = null)
        => new()
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            Name        = name.Trim(),
            Description = description?.Trim(),
            ProductType = productType,
            Status      = TemplateStatus.Draft,
            Version     = 0,
            CreatedAt   = now
        };

    // ── Lifecycle ──────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes the template, making it available for qualification.
    /// Requires at least one question. Increments <see cref="Version"/>.
    /// </summary>
    public Result Publish(DateTimeOffset now)
    {
        if (Status == TemplateStatus.Archived)
            return Result.Fail("TEMPLATE_ALREADY_ARCHIVED");

        if (_questions.Count == 0)
            return Result.Fail("TEMPLATE_HAS_NO_QUESTIONS");

        Status      = TemplateStatus.Published;
        Version    += 1;
        PublishedAt = now;
        return Result.Ok();
    }

    /// <summary>Retires the template. Existing qualification responses are preserved.</summary>
    public Result Archive()
    {
        if (Status == TemplateStatus.Archived)
            return Result.Fail("TEMPLATE_ALREADY_ARCHIVED");

        Status = TemplateStatus.Archived;
        return Result.Ok();
    }

    // ── Editing (Draft only) ───────────────────────────────────────────────

    /// <summary>Updates the template's display fields. Only allowed in <see cref="TemplateStatus.Draft"/>.</summary>
    public Result UpdateDetails(string name, string? description, ProductType? productType)
    {
        if (Status != TemplateStatus.Draft)
            return Result.Fail("TEMPLATE_NOT_IN_DRAFT_STATUS");

        Name        = name.Trim();
        Description = description?.Trim();
        ProductType = productType;
        return Result.Ok();
    }

    /// <summary>Appends a section to the template. Only allowed in <see cref="TemplateStatus.Draft"/>.</summary>
    public Result<QualificationSection> AddSection(string title, string? description)
    {
        if (Status != TemplateStatus.Draft)
            return Result.Fail<QualificationSection>("TEMPLATE_NOT_IN_DRAFT_STATUS");

        var order   = _sections.Count + 1;
        var section = QualificationSection.Create(Id, title, description, order);
        _sections.Add(section);
        return Result.Ok(section);
    }

    /// <summary>Appends a question to the template. Only allowed in <see cref="TemplateStatus.Draft"/>.</summary>
    public Result AddQuestion(
        string label,
        QuestionType type,
        int weight,
        bool isRequired,
        Guid? sectionId = null,
        string[]? options = null,
        string? helpText = null,
        string? placeholderText = null,
        decimal? minValue = null,
        decimal? maxValue = null,
        IReadOnlyList<(Guid TriggerQuestionId, string TriggerValue, QuestionRuleAction Action)>? rules = null)
    {
        if (Status != TemplateStatus.Draft)
            return Result.Fail("TEMPLATE_NOT_IN_DRAFT_STATUS");

        var order = _questions.Count + 1;
        _questions.Add(QualificationQuestion.Create(
            Id, label, type, weight, isRequired, order,
            sectionId, options, helpText, placeholderText, minValue, maxValue, rules));
        return Result.Ok();
    }

    /// <summary>
    /// Clears the question and section collections so the handler can
    /// rebuild them from the update request. Called before <c>ExecuteDeleteAsync</c> in EF.
    /// </summary>
    internal void ClearQuestionsAndSections()
    {
        _questions.Clear();
        _sections.Clear();
    }
}
