namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// An expression tree node that can be serialised to / from JSON and stored in
/// <see cref="WorkflowTransition.ConditionJson"/>.
///
/// Composite nodes (And / Or) carry <see cref="Children"/>.
/// Leaf nodes carry <see cref="Field"/>, <see cref="Operator"/> and <see cref="Value"/>.
///
/// Example — "amount > 5 000 000 AND riskLevel IN [HIGH, MEDIUM]":
/// <code>
/// {
///   "nodeType": "And",
///   "children": [
///     { "nodeType": "Leaf", "field": "amount",    "operator": "Gt",  "value": "5000000"          },
///     { "nodeType": "Leaf", "field": "riskLevel",  "operator": "In",  "value": "[\"HIGH\",\"MEDIUM\"]" }
///   ]
/// }
/// </code>
/// </summary>
public sealed class WorkflowCondition
{
    public ConditionNodeType NodeType { get; init; }

    // ── Leaf-only properties ──────────────────────────────────────────────
    /// <summary>Context key to evaluate (e.g. "amount", "customer.riskLevel").</summary>
    public string? Field { get; init; }

    /// <summary>Comparison operator. Uses the same set as <see cref="RuleOperator"/>.</summary>
    public RuleOperator? Operator { get; init; }

    /// <summary>
    /// Right-hand side value as a string.
    /// For <c>In</c> / <c>NotIn</c>: a JSON string array, e.g. <c>["A","B"]</c>.
    /// For <c>Between</c>: a two-element JSON number array, e.g. <c>[100,200]</c>.
    /// </summary>
    public string? Value { get; init; }

    // ── Composite-only properties ─────────────────────────────────────────
    /// <summary>Child nodes for And / Or composite nodes.</summary>
    public List<WorkflowCondition>? Children { get; init; }
}
