using System.Text.Json;
using System.Text.Json.Serialization;
using Sankore.Modules.Workflow.Domain;

namespace Sankore.Modules.Workflow.Infrastructure.Conditions;

/// <summary>
/// Evaluates a <see cref="WorkflowCondition"/> expression tree stored as JSON against
/// a runtime context dictionary.
/// Null / empty <paramref name="conditionJson"/> always returns true (unconditional).
/// </summary>
internal sealed class ConditionEvaluator : IConditionEvaluator
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public bool Evaluate(string? conditionJson, IReadOnlyDictionary<string, object> context)
    {
        if (string.IsNullOrWhiteSpace(conditionJson))
            return true;

        try
        {
            var node = JsonSerializer.Deserialize<WorkflowCondition>(conditionJson, _jsonOptions);
            return node is not null && EvaluateNode(node, context);
        }
        catch
        {
            // Malformed condition JSON → treat as not-matching to avoid silent pass-through.
            return false;
        }
    }

    private static bool EvaluateNode(WorkflowCondition node, IReadOnlyDictionary<string, object> ctx) =>
        node.NodeType switch
        {
            ConditionNodeType.And  => node.Children?.All(c => EvaluateNode(c, ctx)) ?? false,
            ConditionNodeType.Or   => node.Children?.Any(c => EvaluateNode(c, ctx)) ?? false,
            ConditionNodeType.Leaf => EvaluateLeaf(node, ctx),
            _                      => false
        };

    private static bool EvaluateLeaf(WorkflowCondition node, IReadOnlyDictionary<string, object> ctx)
    {
        if (node.Field is null || node.Operator is null)
            return false;

        if (!ctx.TryGetValue(node.Field, out var raw))
            return node.Operator is RuleOperator.IsEmpty;

        var val  = node.Value ?? string.Empty;

        return node.Operator switch
        {
            RuleOperator.Eq         => StringEqual(raw, val),
            RuleOperator.NotEq      => !StringEqual(raw, val),
            RuleOperator.Gt         => CompareNumeric(raw, val) > 0,
            RuleOperator.Gte        => CompareNumeric(raw, val) >= 0,
            RuleOperator.Lt         => CompareNumeric(raw, val) < 0,
            RuleOperator.Lte        => CompareNumeric(raw, val) <= 0,
            RuleOperator.Contains   => ToString(raw).Contains(val, StringComparison.OrdinalIgnoreCase),
            RuleOperator.In         => ParseList(val).Contains(ToString(raw), StringComparer.OrdinalIgnoreCase),
            RuleOperator.NotIn      => !ParseList(val).Contains(ToString(raw), StringComparer.OrdinalIgnoreCase),
            RuleOperator.IsEmpty    => string.IsNullOrEmpty(ToString(raw)),
            RuleOperator.IsNotEmpty => !string.IsNullOrEmpty(ToString(raw)),
            RuleOperator.Between    => EvaluateBetween(raw, val),
            _                       => false
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool StringEqual(object value, string ruleValue) =>
        string.Equals(ToString(value), ruleValue, StringComparison.OrdinalIgnoreCase);

    private static int CompareNumeric(object value, string ruleValue) =>
        ToDouble(value).CompareTo(ToDouble(ruleValue));

    private static bool EvaluateBetween(object value, string ruleValue)
    {
        try
        {
            var bounds = JsonSerializer.Deserialize<double[]>(ruleValue);
            if (bounds is not { Length: 2 }) return false;
            var v = ToDouble(value);
            return v >= bounds[0] && v <= bounds[1];
        }
        catch { return false; }
    }

    private static List<string> ParseList(string json)
    {
        try   { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string ToString(object value) =>
        value switch
        {
            string s    => s,
            bool b      => b.ToString().ToLowerInvariant(),
            string[] arr => string.Join(",", arr),
            _           => value?.ToString() ?? string.Empty
        };

    private static double ToDouble(object value) =>
        value switch
        {
            double d  => d,
            float f   => f,
            int i     => i,
            long l    => l,
            decimal m => (double)m,
            string s  => double.TryParse(s,
                             System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0,
            _         => 0
        };

    private static double ToDouble(string s) =>
        double.TryParse(s,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
}
