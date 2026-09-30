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
    /// Score at or above which <c>Lead.Qualify</c> marks a lead <c>Qualified</c> rather than
    /// <c>Qualifying</c>. Default: 60 — the historical value, unchanged.
    ///
    /// Lower it only knowingly: <c>LeadScoreCalculator</c> awards 35 of its 100 points from
    /// activity history (interactions 20, behaviour 15), which is empty at capture, so a lead
    /// scored on capture alone peaks at 60 for a walk-in and at 45 for a file import
    /// (<c>LeadSource.FileImport</c> is worth 5 of the 20 source-quality points). A tenant that
    /// wants imported leads to reach dispatching sets something like 35.
    /// </summary>
    public int QualificationThreshold { get; init; } = Domain.Lead.DefaultQualifiedThreshold;

    /// <summary>
    /// When <see langword="true"/>, a freshly captured lead is scored, qualified and then
    /// dispatched automatically by <c>LeadAutoDispatchConsumer</c>. Default:
    /// <see langword="false"/> — turning this on changes what happens to EVERY capture channel
    /// (web forms, webhooks, mobile agents, imports), not just file imports.
    /// </summary>
    public bool AutoDispatchOnCapture { get; init; } = false;

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
