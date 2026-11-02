using System.Text.Json;
using Sankore.Modules.Workflow.PublicApi;

namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// The conditions of a circuit as a tree (product owner, 2026-11-02 : any depth). The steps stay one
/// ordered list — the builder lays a condition, then its « Oui » steps, then its « Non » steps, each
/// branch possibly holding conditions of its own — and the configuration says who belongs where:
/// <c>"node"</c> on a condition (an identifier the builder generates), <c>"parent"</c> + <c>"branch"</c>
/// on each step of its branches.
///
/// <para>
/// An identifier of its own and not the step's id: a new version of the circuit copies the steps
/// with fresh ids but the configuration as it is, and the links must survive the copy. A step with a
/// <c>"branch"</c> and no <c>"parent"</c> was saved before nesting existed: it belongs to the nearest
/// condition before it, which is what the contiguous branches of then meant.
/// </para>
/// </summary>
public static class StepTree
{
    public readonly record struct Step(Guid Id, int Order, StepKind Kind, string? ConfigJson, string Name = "");

    /// <summary>A step's place: the conditions above it, outermost first, with the branch taken in each.</summary>
    public sealed record Chain(IReadOnlyList<(Guid Condition, string Branch)> Links)
    {
        public static readonly Chain Root = new([]);

        public bool IsUnder(Guid condition, string branch) =>
            Links.Any(l => l.Condition == condition && l.Branch == branch);

        public bool IsUnder(Guid condition) => Links.Any(l => l.Condition == condition);

        /// <summary>Whether this place is <paramref name="scope"/> or lies inside it.</summary>
        public bool Within(Chain scope) =>
            Links.Count >= scope.Links.Count && scope.Links.Select((l, i) => Links[i] == l).All(x => x);
    }

    public static string? Node(string? configJson) => Read(configJson, "node");

    public static string? Parent(string? configJson) => Read(configJson, "parent");

    /// <summary>
    /// The place of every step. A step whose <c>"parent"</c> names no condition before it is left at
    /// the root — <see cref="Problem"/> refuses such a circuit at activation.
    /// </summary>
    public static IReadOnlyDictionary<Guid, Chain> Chains(IEnumerable<Step> steps)
    {
        var ordered = steps.OrderBy(s => s.Order).ToList();
        var chains = new Dictionary<Guid, Chain>();
        var nodes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        Step? lastCondition = null;

        foreach (var step in ordered)
        {
            var branch = StepBranch.Of(step.ConfigJson);
            Guid? parent = null;
            if (branch is not null)
            {
                parent = Parent(step.ConfigJson) is { } node
                    ? nodes.TryGetValue(node, out var id) ? id : null
                    : lastCondition?.Id;
            }

            chains[step.Id] = parent is { } p && chains.TryGetValue(p, out var above)
                ? new Chain([.. above.Links, (p, branch!)])
                : Chain.Root;

            if (step.Kind == StepKind.Condition)
            {
                lastCondition = step;
                if (Node(step.ConfigJson) is { } own) nodes.TryAdd(own, step.Id);
            }
        }

        return chains;
    }

    /// <summary>
    /// What makes the circuit unrunnable as drawn, in French, or null: a step attached to a condition
    /// that is not before it, a branch interrupted by a step of another branch, a step placed after a
    /// « Fin » in its own branch (it would never run), a « Fin » that is badly configured.
    /// </summary>
    public static string? Problem(IReadOnlyCollection<Step> steps, string entityType)
    {
        var ordered = steps.OrderBy(s => s.Order).ToList();
        var chains = Chains(ordered);
        var conditions = ordered.Where(s => s.Kind == StepKind.Condition).ToList();

        var nodes = conditions.Select(c => Node(c.ConfigJson)).Where(n => n is not null).ToList();
        if (nodes.Count != nodes.Distinct(StringComparer.Ordinal).Count())
            return "Deux conditions portent le même identifiant : supprimez et recréez l'une d'elles.";

        for (var i = 0; i < ordered.Count; i++)
        {
            var step = ordered[i];
            var branch = StepBranch.Of(step.ConfigJson);

            if (branch is not null && Parent(step.ConfigJson) is { } node
                && !conditions.Any(c => c.Order < step.Order && Node(c.ConfigJson) == node))
                return $"L'étape « {step.Name} » est rattachée à une condition qui n'existe plus ou qui vient après elle.";

            // The branches of a condition follow it without interruption: every step between the
            // condition and one of its steps belongs to that condition too.
            var chain = chains[step.Id];
            if (chain.Links.Count > 0)
            {
                var (condition, _) = chain.Links[^1];
                var start = ordered.FindIndex(s => s.Id == condition);
                if (ordered.Skip(start + 1).Take(i - start - 1).Any(s => !chains[s.Id].IsUnder(condition)))
                    return $"L'étape « {step.Name} » est séparée de sa condition par une étape d'une autre branche.";
            }

            if (step.Kind == StepKind.End)
            {
                if (EndStep.Problem(step.Name, step.ConfigJson, entityType) is { } problem)
                    return problem;

                // A « Fin » closes its branch, or the circuit when it is on the main line.
                if (i + 1 < ordered.Count && chains[ordered[i + 1].Id].Within(chain))
                    return $"L'étape « {ordered[i + 1].Name} » vient après la fin « {step.Name} » : elle ne serait jamais atteinte.";
            }
        }

        return null;
    }

    private static string? Read(string? configJson, string property)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// A « Fin » step's configuration: <c>{"outcome": "approved" | "rejected", "action": "project.win"}</c>.
/// An absent outcome reads as approved — what reaching the end of a circuit always meant.
/// </summary>
public static class EndStep
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static (bool Approved, string? Action) Read(string? configJson)
    {
        string? outcome = null, action = null;
        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String)
                        outcome = o.GetString();
                    if (doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(a.GetString()))
                        action = a.GetString();
                }
            }
            catch (JsonException)
            {
                // Unreadable: the activation check refuses it; an instance already running reads it as approved.
            }
        }
        return (!string.Equals(outcome, Rejected, StringComparison.Ordinal), action);
    }

    public static string? Problem(string stepName, string? configJson, string entityType)
    {
        if (string.Equals(entityType, "KycFile", StringComparison.OrdinalIgnoreCase))
            return $"L'étape « {stepName} » : un circuit KYC se termine par la décision du dossier, pas par une étape « Fin ».";

        var outcome = ReadOutcome(configJson);
        if (outcome is not null and not (Approved or Rejected))
            return $"L'étape « {stepName} » : choisissez si le circuit est validé ou refusé.";

        var (_, action) = Read(configJson);
        if (action is null) return null;

        var definition = WorkflowEndActions.Find(action);
        if (definition is null)
            return $"L'étape « {stepName} » : l'action « {action} » n'existe pas.";
        if (!string.Equals(definition.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            return $"L'étape « {stepName} » : « {definition.Label} » ne s'applique pas à ce type de dossier.";
        return null;
    }

    /// <summary>The history line: « Fin du circuit : validé. Action : Gagner le projet. »</summary>
    public static string Describe(bool approved, string? action)
    {
        var text = approved ? "Fin du circuit : validé." : "Fin du circuit : refusé.";
        return WorkflowEndActions.Find(action) is { } a ? $"{text} Action demandée : {a.Label}." : text;
    }

    private static string? ReadOutcome(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("outcome", out var o)
                    ? o.ValueKind == JsonValueKind.String ? o.GetString() : "?"
                    : null;
        }
        catch (JsonException)
        {
            return "?";
        }
    }
}
