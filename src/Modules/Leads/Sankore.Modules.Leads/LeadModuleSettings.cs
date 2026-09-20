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

    /// <summary>
    /// When <see langword="true"/>, the lead score is automatically
    /// recalculated after each activity is logged.
    /// Default: <see langword="true"/>.
    /// </summary>
    public bool EnableAutoScoreRecalculation { get; init; } = true;

    /// <summary>
    /// Minimum absolute score change (0-100) that triggers a
    /// <see cref="Features.RecalculateLeadScore.Events.LeadScoreCriticallyChangedIntegrationEvent"/>.
    /// Default: 20 points.
    /// </summary>
    public int CriticalScoreChangeDelta { get; init; } = 20;

    /// <summary>
    /// Score thresholds that, when crossed in either direction, trigger a
    /// critical-change notification regardless of <see cref="CriticalScoreChangeDelta"/>.
    /// Default: [40, 60] — the Disqualify / Qualify boundaries.
    /// </summary>
    public int[] CriticalScoreThresholds { get; init; } = [40, 60];

    /// <summary>
    /// Number of days a recycled lead must wait before automatic reactivation.
    /// Default: 30 days.
    /// </summary>
    public int RecycledLeadReactivationDays { get; init; } = 30;

    /// <summary>
    /// Number of days of dormancy (no activity) after which consent must be
    /// re-verified before reactivation (US-M13-161). Default: 90 days.
    /// </summary>
    public int ConsentReverificationDormancyDays { get; init; } = 90;
}
