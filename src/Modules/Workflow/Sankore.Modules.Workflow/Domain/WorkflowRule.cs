using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// A single declarative condition attached to a <see cref="WorkflowStepDefinition"/>.
/// Rules with the same <see cref="LogicalGroup"/> are OR-ed; different groups are AND-ed.
/// </summary>
public sealed class WorkflowRule
{
    public Guid Id           { get; private set; }
    public Guid TemplateId   { get; private set; }
    public Guid StepId       { get; private set; }
    public RuleType RuleType { get; private set; }

    /// <summary>Context key to evaluate (e.g. "Amount", "RequesterRole").</summary>
    public string Field      { get; private set; } = string.Empty;

    public RuleOperator Operator { get; private set; }

    /// <summary>
    /// Comparison value stored as a string.
    /// Numbers: plain decimal (e.g. "500000").
    /// Lists (In/NotIn): JSON array (e.g. "[\"HIGH\",\"MEDIUM\"]").
    /// Range (Between): JSON array with two elements (e.g. "[100,5000]").
    /// </summary>
    public string Value      { get; private set; } = string.Empty;

    /// <summary>
    /// Rules sharing the same group number are OR-ed together.
    /// All groups are then AND-ed. Default group 0 means each rule is its own group.
    /// </summary>
    public int LogicalGroup  { get; private set; }

    private WorkflowRule() { }

    public static WorkflowRule Create(
        Guid templateId,
        Guid stepId,
        RuleType ruleType,
        string field,
        RuleOperator op,
        string value,
        int logicalGroup = 0)
    {
        if (string.IsNullOrWhiteSpace(field))
            throw new DomainException("Rule field is required.");
        if (string.IsNullOrWhiteSpace(value) &&
            op is not RuleOperator.IsEmpty and not RuleOperator.IsNotEmpty)
            throw new DomainException("Rule value is required for this operator.");

        return new WorkflowRule
        {
            Id           = Guid.NewGuid(),
            TemplateId   = templateId,
            StepId       = stepId,
            RuleType     = ruleType,
            Field        = field.Trim(),
            Operator     = op,
            Value        = value,
            LogicalGroup = logicalGroup
        };
    }
}
