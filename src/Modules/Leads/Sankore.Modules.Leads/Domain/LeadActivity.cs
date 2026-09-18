namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Immutable log of a single interaction with a lead (call, meeting, email, visit, note, etc.).
/// Created via <see cref="LogActivity.LogActivityHandler"/>; never mutated after creation.
/// </summary>
public sealed class LeadActivity
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    public ActivityType Type { get; private set; }

    /// <summary>Short summary of the activity (e.g. "Follow-up call", "Demo meeting").</summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>Free-text notes recorded during / after the interaction.</summary>
    public string? Notes { get; private set; }

    /// <summary>Id of the agent/user who performed (or logged) this activity.</summary>
    public Guid PerformedBy { get; private set; }

    /// <summary>When the activity was scheduled (for calls/meetings; null for ad-hoc notes).</summary>
    public DateTimeOffset? ScheduledAt { get; private set; }

    /// <summary>When the activity was actually performed / logged.</summary>
    public DateTimeOffset PerformedAt { get; private set; }

    /// <summary>Duration in minutes (relevant for calls and meetings).</summary>
    public int? DurationMinutes { get; private set; }

    /// <summary>Outcome of the interaction (optional — not all activity types have an outcome).</summary>
    public ActivityOutcome? Outcome { get; private set; }

    private LeadActivity() { } // EF Core

    public static LeadActivity Create(
        Guid tenantId,
        Guid leadId,
        ActivityType type,
        string subject,
        Guid performedBy,
        string? notes = null,
        DateTimeOffset? scheduledAt = null,
        int? durationMinutes = null,
        ActivityOutcome? outcome = null)
        => new()
        {
            Id              = Guid.NewGuid(),
            TenantId        = tenantId,
            LeadId          = leadId,
            Type            = type,
            Subject         = subject.Trim(),
            Notes           = notes?.Trim(),
            PerformedBy     = performedBy,
            ScheduledAt     = scheduledAt,
            PerformedAt     = DateTimeOffset.UtcNow,
            DurationMinutes = durationMinutes,
            Outcome         = outcome,
        };
}
