namespace Sankore.Modules.Workflow.Domain;

/// <summary>Type of node in a <see cref="WorkflowCondition"/> expression tree.</summary>
public enum ConditionNodeType
{
    /// <summary>All child conditions must be true.</summary>
    And,

    /// <summary>At least one child condition must be true.</summary>
    Or,

    /// <summary>A single field comparison (leaf node).</summary>
    Leaf
}
