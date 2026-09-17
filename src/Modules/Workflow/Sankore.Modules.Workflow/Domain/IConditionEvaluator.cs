namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Evaluates a <see cref="WorkflowCondition"/> expression tree serialised as JSON
/// against a runtime context dictionary.
/// Null or empty <paramref name="conditionJson"/> is treated as an unconditional match.
/// </summary>
public interface IConditionEvaluator
{
    bool Evaluate(
        string? conditionJson,
        IReadOnlyDictionary<string, object> context);
}
