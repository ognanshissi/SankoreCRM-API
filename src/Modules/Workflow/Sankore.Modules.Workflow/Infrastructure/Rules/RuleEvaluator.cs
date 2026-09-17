using System.Text.Json;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Rules;

/// <summary>
/// Pure in-memory rule evaluator. No EF / database dependencies.
/// Groups with the same <see cref="WorkflowRule.LogicalGroup"/> are OR-ed;
/// all groups are AND-ed to produce the final result.
/// </summary>
internal sealed class RuleEvaluator : IRuleEvaluator
{
    public bool Evaluate(
        IEnumerable<WorkflowRule> rules,
        IReadOnlyDictionary<string, object> context)
    {
        var groups = rules.GroupBy(r => r.LogicalGroup).ToList();
        if (groups.Count == 0) return false;

        // AND across groups
        foreach (var group in groups)
        {
            // OR within a group
            var groupPassed = group.Any(r => EvaluateLeaf(r, context));
            if (!groupPassed) return false;
        }
        return true;
    }

    private static bool EvaluateLeaf(WorkflowRule rule, IReadOnlyDictionary<string, object> context)
    {
        if (!context.TryGetValue(rule.Field, out var raw))
            return rule.Operator is RuleOperator.IsEmpty;

        return rule.Operator switch
        {
            RuleOperator.Eq         => StringEqual(raw, rule.Value),
            RuleOperator.NotEq      => !StringEqual(raw, rule.Value),
            RuleOperator.Gt         => CompareNumeric(raw, rule.Value) > 0,
            RuleOperator.Gte        => CompareNumeric(raw, rule.Value) >= 0,
            RuleOperator.Lt         => CompareNumeric(raw, rule.Value) < 0,
            RuleOperator.Lte        => CompareNumeric(raw, rule.Value) <= 0,
            RuleOperator.Contains   => ToString(raw).Contains(rule.Value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.In         => ParseList(rule.Value).Contains(ToString(raw), StringComparer.OrdinalIgnoreCase),
            RuleOperator.NotIn      => !ParseList(rule.Value).Contains(ToString(raw), StringComparer.OrdinalIgnoreCase),
            RuleOperator.IsEmpty    => string.IsNullOrEmpty(ToString(raw)),
            RuleOperator.IsNotEmpty => !string.IsNullOrEmpty(ToString(raw)),
            RuleOperator.Between    => EvaluateBetween(raw, rule.Value),
            _                       => false
        };
    }

    private static bool StringEqual(object value, string ruleValue) =>
        string.Equals(ToString(value), ruleValue, StringComparison.OrdinalIgnoreCase);

    private static int CompareNumeric(object value, string ruleValue)
    {
        var left  = ToDouble(value);
        var right = ToDouble(ruleValue);
        return left.CompareTo(right);
    }

    private static bool EvaluateBetween(object value, string ruleValue)
    {
        // ruleValue must be a JSON array: [min, max]
        try
        {
            var bounds = JsonSerializer.Deserialize<double[]>(ruleValue);
            if (bounds is not { Length: 2 }) return false;
            var v = ToDouble(value);
            return v >= bounds[0] && v <= bounds[1];
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ParseList(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static string ToString(object value) =>
        value switch
        {
            string s    => s,
            bool b      => b.ToString().ToLowerInvariant(),
            string[] arr => string.Join(",", arr),
            _           => value?.ToString() ?? string.Empty
        };

    private static double ToDouble(object value)
    {
        return value switch
        {
            double d  => d,
            float f   => f,
            int i     => i,
            long l    => l,
            decimal m => (double)m,
            string s  => double.TryParse(s, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
            _         => 0
        };
    }

    private static double ToDouble(string s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
}
