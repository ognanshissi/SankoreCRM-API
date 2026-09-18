namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Records a prospect's explicit consent for a specific purpose,
/// the channel it was obtained through, supporting evidence, and
/// its lifecycle (Active → Withdrawn).
/// </summary>
public sealed class LeadConsent : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>Purpose for which consent was collected.</summary>
    public ConsentType Type { get; private set; }

    /// <summary>Channel / medium through which consent was obtained.</summary>
    public ConsentChannel Channel { get; private set; }

    public ConsentStatus Status { get; private set; }

    /// <summary>When consent was recorded (UTC).</summary>
    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>
    /// Proof reference: URL to a recorded call, signed document URL,
    /// web-form submission ID, IP address, or any auditable identifier.
    /// </summary>
    public string? ProofReference { get; private set; }

    /// <summary>User who recorded this consent entry.</summary>
    public Guid RecordedBy { get; private set; }

    // ── Withdrawal ────────────────────────────────────────────────────────

    public DateTimeOffset? WithdrawnAt { get; private set; }
    public Guid? WithdrawnBy { get; private set; }
    public string? WithdrawalReason { get; private set; }

    private LeadConsent() { } // EF Core

    public static LeadConsent Create(
        Guid tenantId,
        Guid leadId,
        ConsentType type,
        ConsentChannel channel,
        Guid recordedBy,
        TimeProvider clock,
        string? proofReference = null)
        => new()
        {
            Id             = Guid.NewGuid(),
            TenantId       = tenantId,
            LeadId         = leadId,
            Type           = type,
            Channel        = channel,
            Status         = ConsentStatus.Active,
            GrantedAt      = clock.GetUtcNow(),
            ProofReference = proofReference?.Trim(),
            RecordedBy     = recordedBy
        };

    /// <summary>
    /// Withdraws this consent. Idempotent — returns a failure if already withdrawn.
    /// </summary>
    public Result Withdraw(Guid withdrawnBy, TimeProvider clock, string? reason = null)
    {
        if (Status == ConsentStatus.Withdrawn)
            return Result.Fail("CONSENT_ALREADY_WITHDRAWN");

        Status           = ConsentStatus.Withdrawn;
        WithdrawnAt      = clock.GetUtcNow();
        WithdrawnBy      = withdrawnBy;
        WithdrawalReason = reason?.Trim();
        return Result.Ok();
    }
}
