namespace Sankore.Shared.Infrastructure.Workflow;

/// <summary>
/// Extension point for entity-specific workflow context enrichment.
/// Each business module registers one implementation per entity type it owns.
/// The Workflow module resolves all registered providers at instance-start time
/// and calls the matching one to hydrate the context dictionary.
/// </summary>
public interface IContextProvider
{
    /// <summary>
    /// The entity type name this provider handles, e.g. <c>"Lead"</c>, <c>"Loan"</c>.
    /// Must match the <c>EntityType</c> field used in workflow templates.
    /// </summary>
    string EntityType { get; }

    /// <summary>
    /// Builds a flat key→value context dictionary for the given entity.
    /// Values may be <see cref="string"/>, <see cref="double"/>, <see cref="bool"/>,
    /// or <see cref="string"/>[] — the same types understood by the rule and condition evaluators.
    /// Caller-supplied overrides are merged on top after this method returns.
    /// </summary>
    Task<Dictionary<string, object>> BuildAsync(
        Guid entityId,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The keys <see cref="BuildAsync"/> fills, described for the condition editor of the journey
    /// builder (« Condition » step): a condition names one of these, never a free key nobody fills.
    /// </summary>
    IReadOnlyList<ContextField> Fields { get; }
}

/// <param name="Type"><c>text</c>, <c>number</c>, <c>choice</c> or <c>boolean</c> — decides the operators offered.</param>
/// <param name="Options">For a <c>choice</c>: the values the key can hold, with their French label.</param>
public sealed record ContextField(
    string Key,
    string Label,
    string Type,
    IReadOnlyList<ContextFieldOption>? Options = null)
{
    public static ContextField Text(string key, string label) => new(key, label, "text");
    public static ContextField Number(string key, string label) => new(key, label, "number");
    public static ContextField Boolean(string key, string label) => new(key, label, "boolean");
    public static ContextField Choice(string key, string label, params (string Value, string Label)[] options)
        => new(key, label, "choice", options.Select(o => new ContextFieldOption(o.Value, o.Label)).ToList());
}

public sealed record ContextFieldOption(string Value, string Label);
