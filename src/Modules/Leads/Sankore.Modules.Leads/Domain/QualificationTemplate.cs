namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-configurable qualification questionnaire scoped to a financial product.
/// Lifecycle: Draft → Published → Archived.
/// Only one <see cref="TemplateStatus.Published"/> template can exist per
/// <see cref="ProductCategory"/> per tenant at any time.
/// </summary>
public sealed class QualificationTemplate : AggregateRoot
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>Financial product category this template is scoped to. Null = generic (not product-specific).</summary>
    public ProductCategory? ProductCategory { get; private set; }

    /// <summary>Specific product code (e.g. "CRED-AGRI-01"). Null = category-level or generic template.</summary>
    public string? ProductCode { get; private set; }

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
        ProductCategory? productCategory = null,
        string? productCode = null)
        => new()
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            Name        = name.Trim(),
            Description = description?.Trim(),
            ProductCategory = productCategory,
            ProductCode = productCode?.Trim().ToUpperInvariant(),
            Status      = TemplateStatus.Draft,
            Version     = 0,
            CreatedAt   = now
        };

    // ── Lifecycle ──────────────────────────────────────────────────────────

    /// <summary>
    /// Publishes the template, making it available for qualification.
    /// Allowed from every status: from Draft for a first release or a new revision, and
    /// from Archived to put a retired version back in service without re-editing it.
    /// Requires at least one question. Increments <see cref="Version"/>.
    /// </summary>
    public Result Publish(DateTimeOffset now)
    {
        var canPublish = CanPublish();
        if (canPublish.IsFailure)
            return canPublish;

        Status      = TemplateStatus.Published;
        Version    += 1;
        PublishedAt = now;
        return Result.Ok();
    }

    /// <summary>
    /// Whether <see cref="Publish"/> would succeed, without mutating anything. Lets a caller
    /// check first when publishing means making other writes that would otherwise need undoing.
    /// </summary>
    public Result CanPublish()
        => _questions.Count == 0
            ? Result.Fail("TEMPLATE_HAS_NO_QUESTIONS")
            : Result.Ok();

    /// <summary>Reverts a Published template back to Draft for editing. Must be re-published after.</summary>
    /// <remarks>
    /// Deliberately silent on an Archived template: editing one must not quietly bring it
    /// back. Restoring is an explicit act — see <see cref="Unarchive"/>.
    /// </remarks>
    public void RevertToDraft()
    {
        if (Status != TemplateStatus.Archived)
            Status = TemplateStatus.Draft;
    }

    /// <summary>Retires the template. Existing qualification responses are preserved.</summary>
    public Result Archive()
    {
        if (Status == TemplateStatus.Archived)
            return Result.Fail("TEMPLATE_ALREADY_ARCHIVED");

        Status = TemplateStatus.Archived;
        return Result.Ok();
    }

    /// <summary>
    /// Brings an archived template back as an editable <see cref="TemplateStatus.Draft"/>.
    /// Nothing goes live here — publishing stays a separate, deliberate step.
    /// </summary>
    /// <remarks>
    /// <see cref="Version"/> and <see cref="PublishedAt"/> are left untouched: they record
    /// what this template has already been through, and the next publish advances them.
    /// </remarks>
    public Result Unarchive()
    {
        if (Status != TemplateStatus.Archived)
            return Result.Fail("TEMPLATE_NOT_ARCHIVED");

        Status = TemplateStatus.Draft;
        return Result.Ok();
    }

    /// <summary>
    /// Deep-copies this template into a new <see cref="TemplateStatus.Draft"/> one,
    /// whatever status this template is in — duplicating a Published or Archived
    /// template is how an administrator starts the next revision of it.
    /// The copy keeps the product scope but starts at version 0, unpublished.
    /// </summary>
    /// <param name="name">Name for the copy. Templates are not uniquely named, so the caller chooses.</param>
    public QualificationTemplate Duplicate(string name, DateTimeOffset now)
    {
        var copy = Create(TenantId, name, now, Description, ProductCategory, ProductCode);

        // Sections first: every question carries a SectionId that has to point at the
        // copy's own section rather than the source's.
        var sectionIdMap = new Dictionary<Guid, Guid>();
        foreach (var section in _sections.OrderBy(s => s.Order))
        {
            var clonedSection = QualificationSection.Create(
                copy.Id, section.Title, section.Description, section.Order);

            sectionIdMap[section.Id] = clonedSection.Id;
            copy._sections.Add(clonedSection);
        }

        var questionIdMap = new Dictionary<Guid, Guid>();
        foreach (var question in _questions.OrderBy(q => q.Order))
        {
            var clonedQuestion = question.CloneInto(copy.Id, sectionIdMap);

            questionIdMap[question.Id] = clonedQuestion.Id;
            copy._questions.Add(clonedQuestion);
        }

        // Rules trigger off sibling questions, and a rule can point forwards as easily as
        // backwards — so they can only be rewired once every question has its new id.
        foreach (var clonedQuestion in copy._questions)
            clonedQuestion.RemapRuleTriggers(questionIdMap);

        return copy;
    }

    // ── Editing (Draft only) ───────────────────────────────────────────────

    /// <summary>Updates the template's display fields. Only allowed in <see cref="TemplateStatus.Draft"/>.</summary>
    public Result UpdateDetails(string name, string? description, ProductCategory? productCategory, string? productCode = null)
    {
        if (Status != TemplateStatus.Draft)
            return Result.Fail("TEMPLATE_NOT_IN_DRAFT_STATUS");

        Name        = name.Trim();
        Description = description?.Trim();
        ProductCategory = productCategory;
        ProductCode = productCode?.Trim().ToUpperInvariant();
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
