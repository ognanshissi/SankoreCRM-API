namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>What happened to a project, for its « Historique » (owner, 2026-10-27).</summary>
public enum ProjectHistoryKind
{
    Created,
    StepChanged,
    AmountChanged,
    ProductChanged,
    CloseDateChanged,
    TemplateVersionChanged,
    Won,
    Lost,
    HoldingOpened,
    WorkflowRestarted,
}

/// <summary>
/// One line of a project's history: what changed, from what to what, who did it and when. Written
/// by <c>ProjectHistoryRecorder</c> next to the change, in the same save; never edited.
///
/// <para>
/// <b>Values are stored as they read at the time</b> — a step's label, a product's name — and not
/// as ids resolved later: a step renamed or removed by a newer template version, or a product
/// renamed in the catalogue, must not rewrite what the agent saw when it happened. Their shape per
/// kind is on <see cref="From"/> and <see cref="To"/>.
/// </para>
/// </summary>
public sealed class ProjectHistoryEntry : ITenant
{
    public const int ValueMaxLength = 200;
    public const int DetailMaxLength = 500;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public ProjectHistoryKind Kind { get; private set; }
    public DateTimeOffset At { get; private set; }

    /// <summary>Who did it; null for the system (a job, a consumer) and for a reconstructed line.</summary>
    public Guid? ActorId { get; private set; }

    /// <summary>
    /// The value before: a step label, a product, an amount in invariant digits (« 90000 »), a
    /// date (« 2026-12-01 »), a version number (« 2 »). Null when there was none.
    /// </summary>
    public string? From { get; private set; }

    /// <summary>The value after, in the same shape as <see cref="From"/>.</summary>
    public string? To { get; private set; }

    /// <summary>
    /// What else the line needs: the currency of an amount, the template of a version, the reason
    /// of a won or lost, the circuit restarted, the product at creation.
    /// </summary>
    public string? Detail { get; private set; }

    /// <summary>The record the line points at — the contract opened.</summary>
    public Guid? RelatedId { get; private set; }

    /// <summary>
    /// Rebuilt by the migration from what the project already stored (its creation, its closing),
    /// for a project older than the history: the moment is right, the author is unknown.
    /// </summary>
    public bool Reconstructed { get; private set; }

    private ProjectHistoryEntry() { }

    public static ProjectHistoryEntry Record(
        Opportunity project, ProjectHistoryKind kind, Guid? actorId, DateTimeOffset at,
        string? from = null, string? to = null, string? detail = null, Guid? relatedId = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = project.TenantId,
        OpportunityId = project.Id,
        Kind = kind,
        At = at,
        ActorId = actorId == Guid.Empty ? null : actorId,
        From = Clip(from, ValueMaxLength),
        To = Clip(to, ValueMaxLength),
        Detail = Clip(detail, DetailMaxLength),
        RelatedId = relatedId,
    };

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
