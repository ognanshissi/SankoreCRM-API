using Sankore.Modules.Workflow.Domain.Events;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Defines the approval circuit for a given entity type within a tenant.
/// A tenant can have at most one active template per EntityType.
/// </summary>
public sealed class WorkflowTemplate : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Entity type this template applies to (e.g. "Lead", "Opportunity").</summary>
    public string EntityType { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private set; }

    private readonly List<WorkflowStepDefinition> _steps = [];
    public IReadOnlyCollection<WorkflowStepDefinition> Steps => _steps.AsReadOnly();

    private WorkflowTemplate() { }

    public static WorkflowTemplate Create(
        Guid tenantId,
        string entityType,
        string name,
        Guid createdByUserId,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(entityType))
            throw new DomainException("EntityType is required.");
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Template name is required.");

        var now = DateTimeOffset.UtcNow;
        return new WorkflowTemplate
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntityType = entityType.Trim(),
            Name = name.Trim(),
            Description = description?.Trim(),
            IsActive = false,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = createdByUserId
        };
    }

    public void Update(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("Template name is required.");

        Name = name.Trim();
        Description = description?.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Activate()
    {
        if (_steps.Count == 0)
            throw new DomainException("Cannot activate a template with no steps.");
        IsActive = true;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public WorkflowStepDefinition AddStep(
        int order,
        string name,
        string? description = null,
        string? approverRoleCode = null,
        int? timeoutHours = null)
    {
        if (_steps.Any(s => s.Order == order))
            throw new DomainException($"A step with order {order} already exists.");

        var step = WorkflowStepDefinition.Create(Id, order, name, description, approverRoleCode, timeoutHours);
        _steps.Add(step);
        UpdatedAt = DateTimeOffset.UtcNow;
        return step;
    }

    public void RemoveStep(Guid stepId)
    {
        var step = _steps.FirstOrDefault(s => s.Id == stepId)
            ?? throw new DomainException("Step not found.");
        _steps.Remove(step);
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}
