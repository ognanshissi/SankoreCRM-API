namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Owned entity — lives inside a <see cref="WorkflowTemplate"/>.
/// Describes one step in the approval circuit (order, role required, timeout).
/// </summary>
public sealed class WorkflowStepDefinition
{
    public Guid Id { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>1-based execution order within the template.</summary>
    public int Order { get; private set; }

    /// <summary>Machine-readable state code (e.g. "STEP_1"). Auto-set from Order.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Semantic type of this node in the state machine.</summary>
    public StateType StateType { get; private set; } = StateType.Approval;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Role code (e.g. "branchmanager") required to approve this step.</summary>
    public string? ApproverRoleCode { get; private set; }

    /// <summary>Optional SLA — how many hours the approver has to act.</summary>
    public int? TimeoutHours { get; private set; }

    private readonly List<WorkflowRule> _rules = [];
    public IReadOnlyCollection<WorkflowRule> Rules => _rules.AsReadOnly();

    private WorkflowStepDefinition() { }

    public static WorkflowStepDefinition Create(
        Guid templateId,
        int order,
        string name,
        string? description = null,
        string? approverRoleCode = null,
        int? timeoutHours = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new Sankore.Shared.Kernel.DomainException("Step name is required.");
        if (order < 1)
            throw new Sankore.Shared.Kernel.DomainException("Step order must be >= 1.");

        return new WorkflowStepDefinition
        {
            Id               = Guid.NewGuid(),
            TemplateId       = templateId,
            Order            = order,
            Code             = $"STEP_{order}",
            StateType        = StateType.Approval,
            Name             = name.Trim(),
            Description      = description?.Trim(),
            ApproverRoleCode = approverRoleCode,
            TimeoutHours     = timeoutHours
        };
    }

    public WorkflowRule AddRule(RuleType ruleType, string field, RuleOperator op, string value, int logicalGroup = 0)
    {
        var rule = WorkflowRule.Create(TemplateId, Id, ruleType, field, op, value, logicalGroup);
        _rules.Add(rule);
        return rule;
    }

    public void RemoveRule(Guid ruleId)
    {
        var rule = _rules.FirstOrDefault(r => r.Id == ruleId)
            ?? throw new Sankore.Shared.Kernel.DomainException("Rule not found.");
        _rules.Remove(rule);
    }

    public void Update(string name, string? description, string? approverRoleCode, int? timeoutHours)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new Sankore.Shared.Kernel.DomainException("Step name is required.");

        Name = name.Trim();
        Description = description?.Trim();
        ApproverRoleCode = approverRoleCode;
        TimeoutHours = timeoutHours;
    }
}
