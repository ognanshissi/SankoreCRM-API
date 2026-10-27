namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-configurable qualification questionnaire scoped to a financial product.
/// Lifecycle: Draft → Published → Archived.
/// Only one <see cref="TemplateStatus.Published"/> template can exist per tenant for a given
/// (<see cref="ProductCategory"/>, <see cref="ProductCode"/>) at any time — several per family when
/// their products differ (owner, 2026-10-27).
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

    // ── Multi-person questionnaires (BNF-B-07) ──────────────────────────────

    /// <summary>
    /// Who this questionnaire is answered about (BNF-B-07). Defaults to
    /// <see cref="QualificationSubjectScope.Lead"/>, which is the pre-existing behaviour — every
    /// template written before this evolution keeps it and behaves exactly as it did.
    /// </summary>
    public QualificationSubjectScope SubjectScope { get; private set; } = QualificationSubjectScope.Lead;

    /// <summary>
    /// Whether this questionnaire collects health data (BNF-B-09).
    ///
    /// <para>
    /// Setting it forces <see cref="ExcludeFromScoring"/> true and makes it unsettable — see
    /// <see cref="SetSubjectScope"/>. It also changes how the answers are STORED: encrypted in
    /// bulk, with no blind index, and readable only with <c>lead:health:read</c>.
    /// </para>
    /// </summary>
    public bool IsHealthQuestionnaire { get; private set; }

    /// <summary>
    /// Whether the answers are kept out of the F13.6 score (BNF-B-07).
    ///
    /// <para>
    /// Always true for a health questionnaire, and not negotiable: BNF-B-09 forbids health data
    /// from reaching the scoring or the dispatching, and a flag an administrator could untick
    /// would make that a configuration rather than a rule. An architecture test pins the
    /// structural half.
    /// </para>
    /// </summary>
    public bool ExcludeFromScoring { get; private set; }

    /// <summary>
    /// True when this questionnaire produces one response PER PERSON rather than one per lead.
    /// </summary>
    public bool IsPerPerson => SubjectScope != QualificationSubjectScope.Lead;

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

    /// <summary>
    /// Categories a HEALTH questionnaire may be attached to (BNF-B-07: "une ligne de produit de
    /// catégorie Insurance").
    ///
    /// <para>
    /// THREE values, not one. The Kernel's <c>ProductCategory</c> splits insurance into
    /// <c>Insurance</c>, <c>HealthInsurance</c> and <c>ForecastInsurance</c>, and a health
    /// questionnaire is most obviously for the second of those — reading the specification's
    /// "Insurance" as the single enum member of that name would refuse the very product it was
    /// written for.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Fully qualified, because this class has an INSTANCE property named <c>ProductCategory</c>
    /// which shadows the enum type — the unqualified form resolves to the property and does not
    /// compile.
    /// </remarks>
    private static readonly Sankore.Shared.Kernel.ProductCategory[] HealthEligibleCategories =
    [
        Sankore.Shared.Kernel.ProductCategory.Insurance,
        Sankore.Shared.Kernel.ProductCategory.HealthInsurance,
        Sankore.Shared.Kernel.ProductCategory.ForecastInsurance,
    ];

    /// <summary>
    /// Sets the questionnaire's subject scope and its health flag (BNF-B-07). Draft only, like
    /// every other edit.
    /// </summary>
    /// <param name="excludeFromScoring">
    /// Ignored when <paramref name="isHealthQuestionnaire"/> is true — it is then forced on.
    /// </param>
    /// <returns>
    /// <c>TEMPLATE_NOT_IN_DRAFT_STATUS</c>, or <c>HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE</c>
    /// when a health questionnaire is scoped to a category that is not an insurance one.
    /// </returns>
    public Result SetSubjectScope(
        QualificationSubjectScope subjectScope,
        bool isHealthQuestionnaire,
        bool excludeFromScoring)
    {
        if (Status != TemplateStatus.Draft)
            return Result.Fail("TEMPLATE_NOT_IN_DRAFT_STATUS");

        if (isHealthQuestionnaire)
        {
            // A health questionnaire with no category at all is refused too: "generic health
            // questions" would be collected from every lead of every product line, which is the
            // opposite of what a purpose limitation means.
            if (ProductCategory is not { } category || !HealthEligibleCategories.Contains(category))
                return Result.Fail("HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE");
        }

        SubjectScope          = subjectScope;
        IsHealthQuestionnaire = isHealthQuestionnaire;

        // Forced, not merely defaulted (BNF-B-07). Health answers must never reach the score, and
        // a flag an administrator could untick would make that a setting rather than a rule.
        ExcludeFromScoring = isHealthQuestionnaire || excludeFromScoring;

        return Result.Ok();
    }

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

        // Carried onto the copy. A duplicated health questionnaire that came back as a scoring
        // one, answered per lead, would be the most dangerous possible default — the copy is made
        // precisely to start the next revision of THIS questionnaire.
        copy.SubjectScope          = SubjectScope;
        copy.IsHealthQuestionnaire = IsHealthQuestionnaire;
        copy.ExcludeFromScoring    = ExcludeFromScoring;

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

        // Checked BEFORE the category moves: a health questionnaire re-scoped onto a savings
        // line would otherwise keep collecting health answers under a product that has no business
        // asking for them, and SetSubjectScope — the only place that rule lives — is not involved
        // in this call.
        if (IsHealthQuestionnaire
            && (productCategory is not { } moved || !HealthEligibleCategories.Contains(moved)))
            return Result.Fail("HEALTH_QUESTIONNAIRE_REQUIRES_INSURANCE_LINE");

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
