namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tenant-configurable qualification questionnaire.
/// Each template holds an ordered list of weighted questions; the scorer
/// normalises the earned weights to a 0-100 score.
/// </summary>
public sealed class QualificationTemplate : AggregateRoot
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string? Description { get; private set; }

    /// <summary>
    /// Optional product filter. When set, only leads interested in this product
    /// are shown this template in the UI (not enforced server-side).
    /// </summary>
    public string? ProductName { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<QualificationQuestion> _questions = [];
    public IReadOnlyList<QualificationQuestion> Questions => _questions.AsReadOnly();

    private QualificationTemplate() { } // EF Core

    public static QualificationTemplate Create(
        Guid tenantId,
        string name,
        DateTimeOffset now,
        string? description = null,
        string? productName = null)
        => new()
        {
            Id          = Guid.NewGuid(),
            TenantId    = tenantId,
            Name        = name.Trim(),
            Description = description?.Trim(),
            ProductName = productName?.Trim(),
            IsActive    = true,
            CreatedAt   = now
        };

    /// <summary>Appends a question to the end of the template.</summary>
    public void AddQuestion(
        string label,
        QuestionType type,
        int weight,
        bool isRequired,
        string[]? options = null)
    {
        var order = _questions.Count + 1;
        _questions.Add(QualificationQuestion.Create(Id, label, type, weight, isRequired, order, options));
    }

    public void Deactivate() => IsActive = false;
}
