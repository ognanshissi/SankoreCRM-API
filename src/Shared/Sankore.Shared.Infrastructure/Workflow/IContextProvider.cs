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
}
