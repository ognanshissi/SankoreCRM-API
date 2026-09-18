namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A scheduled follow-up reminder attached to a lead.
/// Can be completed or dismissed; completed reminders refresh
/// <see cref="Lead.LastActivityAt"/> via <see cref="Lead.RecordActivity"/>.
/// </summary>
public sealed class LeadReminder
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>Short title displayed in dashboards (e.g. "Call back about loan terms").</summary>
    public string Title { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    /// <summary>When the reminder is due.</summary>
    public DateTimeOffset DueAt { get; private set; }

    /// <summary>User who created the reminder.</summary>
    public Guid CreatedBy { get; private set; }

    public ReminderStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the reminder was completed or dismissed (null while Pending).</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    private LeadReminder() { } // EF Core

    public static LeadReminder Create(
        Guid tenantId,
        Guid leadId,
        string title,
        Guid createdBy,
        DateTimeOffset dueAt,
        string? notes = null)
        => new()
        {
            Id        = Guid.NewGuid(),
            TenantId  = tenantId,
            LeadId    = leadId,
            Title     = title.Trim(),
            Notes     = notes?.Trim(),
            DueAt     = dueAt,
            CreatedBy = createdBy,
            Status    = ReminderStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    public Result Complete()
    {
        if (Status != ReminderStatus.Pending)
            return Result.Fail("Only a Pending reminder can be completed.");

        Status     = ReminderStatus.Completed;
        ResolvedAt = DateTimeOffset.UtcNow;
        return Result.Ok();
    }

    public Result Dismiss()
    {
        if (Status != ReminderStatus.Pending)
            return Result.Fail("Only a Pending reminder can be dismissed.");

        Status     = ReminderStatus.Dismissed;
        ResolvedAt = DateTimeOffset.UtcNow;
        return Result.Ok();
    }
}
