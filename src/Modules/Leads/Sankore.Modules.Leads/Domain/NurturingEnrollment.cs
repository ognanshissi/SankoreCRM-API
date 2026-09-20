namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Tracks a lead's enrollment into a nurturing sequence (US-M13-150/151).
/// The Hangfire job reads active enrollments to determine which step to send next.
/// </summary>
public sealed class NurturingEnrollment : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid SequenceId { get; private set; }

    /// <summary>Zero-based index of the last completed step (-1 = not started).</summary>
    public int LastCompletedStepIndex { get; private set; }

    public NurturingEnrollmentStatus Status { get; private set; }
    public DateTimeOffset EnrolledAt { get; private set; }

    /// <summary>When the next step becomes eligible for sending.</summary>
    public DateTimeOffset NextStepDueAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancellationReason { get; private set; }

    private NurturingEnrollment() { }

    public static NurturingEnrollment Create(
        Guid tenantId,
        Guid leadId,
        Guid sequenceId,
        TimeSpan firstStepDelay,
        TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        return new()
        {
            Id                     = Guid.NewGuid(),
            TenantId               = tenantId,
            LeadId                 = leadId,
            SequenceId             = sequenceId,
            LastCompletedStepIndex = -1,
            Status                 = NurturingEnrollmentStatus.Active,
            EnrolledAt             = now,
            NextStepDueAt          = now.Add(firstStepDelay),
        };
    }

    /// <summary>Advances to the next step after a successful send.</summary>
    public void AdvanceStep(TimeSpan nextDelay, TimeProvider clock)
    {
        LastCompletedStepIndex++;
        NextStepDueAt = clock.GetUtcNow().Add(nextDelay);
    }

    /// <summary>Marks the sequence as fully completed (all steps sent).</summary>
    public void Complete(TimeProvider clock)
    {
        LastCompletedStepIndex++;
        Status      = NurturingEnrollmentStatus.Completed;
        CompletedAt = clock.GetUtcNow();
    }

    /// <summary>Immediately cancels the enrollment (consent withdrawal, manual stop, etc.).</summary>
    public void Cancel(string reason, TimeProvider clock)
    {
        if (Status != NurturingEnrollmentStatus.Active) return;

        Status             = NurturingEnrollmentStatus.Cancelled;
        CancelledAt        = clock.GetUtcNow();
        CancellationReason = reason;
    }
}

public enum NurturingEnrollmentStatus
{
    Active,
    Completed,
    Cancelled
}
