namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Evaluates a set of <see cref="WorkflowRule"/> records against a runtime context.
/// Rules sharing the same <see cref="WorkflowRule.LogicalGroup"/> are OR-ed;
/// all groups are AND-ed to produce the final result.
/// </summary>
public interface IRuleEvaluator
{
    bool Evaluate(
        IEnumerable<WorkflowRule> rules,
        IReadOnlyDictionary<string, object> context);
}
