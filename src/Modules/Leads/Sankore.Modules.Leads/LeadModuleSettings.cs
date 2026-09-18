namespace Sankore.Modules.Leads;

/// <summary>
/// Module-level configuration for the Leads module.
/// Bind via <c>appsettings.json</c> section <c>"Leads"</c>.
/// </summary>
public sealed class LeadModuleSettings
{
    /// <summary>
    /// When <see langword="true"/>, a non-null, non-whitespace
    /// <c>Reason</c> is required when dismissing a duplicate pair.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool RequireDismissalReason { get; init; } = false;
}
