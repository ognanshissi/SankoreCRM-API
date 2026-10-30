using System.Globalization;
using System.Text.Json;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Infrastructure.Waits;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Workflow.Infrastructure.Branching;

/// <summary>
/// A « Condition » step opened. Workflow-internal — written to its own outbox by
/// <c>WorkflowDbContext.SaveChanges</c> and consumed by <see cref="ConditionStepConsumer"/>, so the
/// condition is evaluated on the record AS IT IS NOW (the module that owns it builds the facts),
/// not on the context snapshot taken when the instance started.
/// </summary>
public sealed record ConditionStepReached(Guid TenantId, Guid InstanceId, Guid InstanceStepId) : IntegrationEventBase;

/// <summary>
/// A « Condition » step's configuration: <c>{ "condition": { "nodeType": "leaf", "field": "score",
/// "operator": "gte", "value": "60" } }</c> — one comparison on a field the record's
/// <see cref="IContextProvider"/> fills. The steps right after it carrying <c>"branch": "yes"</c> /
/// <c>"no"</c> are its two branches.
/// </summary>
internal static class ConditionStepConfig
{
    public sealed record Leaf(string Field, string Operator, string? Value, string RawJson);

    public static Leaf? Read(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("condition", out var node)
                || node.ValueKind != JsonValueKind.Object)
                return null;

            var field = Text(node, "field");
            var op = Text(node, "operator");
            if (field is null || op is null) return null;
            return new Leaf(field, op, Text(node, "value"), node.GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why this condition could never be evaluated, in French — or null.</summary>
    public static string? Problem(string stepName, string? configJson, IContextProvider? provider)
    {
        if (provider is null)
            return $"L'étape « {stepName} » : ce type de dossier ne fournit pas de données à comparer.";
        var leaf = Read(configJson);
        if (leaf is null)
            return $"L'étape « {stepName} » n'a pas de condition.";
        if (provider.Fields.All(f => !string.Equals(f.Key, leaf.Field, StringComparison.OrdinalIgnoreCase)))
            return $"L'étape « {stepName} » compare un champ inconnu ({leaf.Field}).";
        if (!Enum.TryParse<RuleOperator>(leaf.Operator, ignoreCase: true, out var op))
            return $"L'étape « {stepName} » utilise un opérateur inconnu ({leaf.Operator}).";
        if (op is not (RuleOperator.IsEmpty or RuleOperator.IsNotEmpty) && string.IsNullOrWhiteSpace(leaf.Value))
            return $"L'étape « {stepName} » compare à une valeur vide.";
        return null;
    }

    /// <summary>« Score du lead ≥ 60 : oui (valeur : 72) → branche Oui. »</summary>
    public static string Reason(Leaf leaf, bool outcome, object? actual, IContextProvider provider)
    {
        var field = provider.Fields.FirstOrDefault(f => string.Equals(f.Key, leaf.Field, StringComparison.OrdinalIgnoreCase));
        var label = field?.Label ?? leaf.Field;
        var expected = Display(field, leaf.Value);
        var found = actual is null || actual is string { Length: 0 } ? "vide" : Display(field, Convert.ToString(actual, CultureInfo.InvariantCulture));
        var test = Symbol(leaf.Operator) is { } symbol ? $"{label} {symbol} {expected}".TrimEnd() : label;
        return $"{test} : {(outcome ? "oui" : "non")} (valeur : {found}) → branche {(outcome ? "Oui" : "Non")}.";
    }

    private static string? Symbol(string op) => op.ToLowerInvariant() switch
    {
        "eq" => "=", "noteq" => "≠", "gt" => ">", "gte" => "≥", "lt" => "<", "lte" => "≤",
        "contains" => "contient", "in" => "parmi", "notin" => "hors de",
        "isempty" => "est vide", "isnotempty" => "est renseigné",
        _ => null,
    };

    private static string Display(ContextField? field, string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (field?.Type == "boolean") return value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "oui" : "non";
        // « 1 000 000 » plutôt que « 1000000 » : la ligne d'historique est lue par une personne.
        if (field?.Type == "number" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number.ToString("#,##0.##", CultureInfo.GetCultureInfo("fr-FR"));
        return field?.Options?.FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;
    }

    private static string? Text(JsonElement node, string name)
        => node.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())?.Trim() is { Length: > 0 } t ? t : null
            : null;
}

/// <summary>
/// Evaluates a « Condition » step on the record's current facts and settles it
/// (<see cref="WorkflowInstance.ResolveCondition"/>): the losing branch is skipped and the circuit
/// moves on. A record whose facts cannot be read takes the « Non » branch, and the history says why.
/// </summary>
public sealed class ConditionStepConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<ConditionStepConsumer> logger) : IConsumer<ConditionStepReached>
{
    public async Task Consume(ConsumeContext<ConditionStepReached> context)
    {
        var reached = context.Message;
        var ct = context.CancellationToken;
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WorkflowDbContext>();

        var target = await db.WorkflowInstanceSteps
            .IgnoreQueryFilters()
            .Where(s => s.TenantId == reached.TenantId && s.Id == reached.InstanceStepId
                     && s.Kind == StepKind.Condition && s.Status == StepStatus.AwaitingApproval)
            .Join(db.WorkflowInstances.IgnoreQueryFilters(), s => s.InstanceId, i => i.Id,
                (s, i) => new { s.ConfigJson, i.EntityType, i.EntityId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return; // already settled (redelivery) or closed by hand

        var provider = sp.GetServices<IContextProvider>()
            .FirstOrDefault(p => string.Equals(p.EntityType, target.EntityType, StringComparison.OrdinalIgnoreCase));
        var leaf = ConditionStepConfig.Read(target.ConfigJson);

        bool outcome;
        string reason;
        if (provider is null || leaf is null)
        {
            outcome = false;
            reason = "Condition impossible à évaluer (données du dossier indisponibles) → branche Non.";
        }
        else
        {
            var facts = await provider.BuildAsync(target.EntityId, reached.TenantId, ct);
            if (facts.Count == 0)
            {
                outcome = false;
                reason = "Le dossier est introuvable : condition non évaluée → branche Non.";
            }
            else
            {
                outcome = sp.GetRequiredService<IConditionEvaluator>().Evaluate(leaf.RawJson, facts);
                facts.TryGetValue(leaf.Field, out var actual);
                reason = ConditionStepConfig.Reason(leaf, outcome, actual, provider);
            }
        }

        var moved = await sp.GetRequiredService<InstanceRunner>()
            .ResolveConditionAsync(reached.InstanceId, reached.InstanceStepId, outcome, reason, ct);
        logger.LogInformation("Condition step {StepId}: {Reason} (moved: {Moved})", reached.InstanceStepId, reason, moved);
    }
}
