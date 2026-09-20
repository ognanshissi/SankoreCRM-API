using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Domain;

/// <summary>
/// Tenant-scoped SLA configuration defining response-time expectations
/// for lead handling. When <see cref="AgencyId"/> is null the config
/// acts as the tenant-wide default; a non-null value overrides for that
/// specific agency.
/// </summary>
public sealed class SlaConfig : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Null = tenant-wide default. Non-null = agency-level override.
    /// </summary>
    public Guid? AgencyId { get; private set; }

    public string Name { get; private set; } = default!;

    /// <summary>Max time to first contact after assignment.</summary>
    public TimeSpan FirstContactDeadline { get; private set; }

    /// <summary>Max time to qualify after capture.</summary>
    public TimeSpan QualificationDeadline { get; private set; }

    /// <summary>Max time between follow-ups.</summary>
    public TimeSpan FollowUpDeadline { get; private set; }

    /// <summary>Time before auto-escalation.</summary>
    public TimeSpan EscalationDeadline { get; private set; }

    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private SlaConfig() { } // EF Core

    public static SlaConfig Create(
        Guid tenantId,
        Guid? agencyId,
        string name,
        TimeSpan firstContactDeadline,
        TimeSpan qualificationDeadline,
        TimeSpan followUpDeadline,
        TimeSpan escalationDeadline)
        => new()
        {
            Id                    = Guid.NewGuid(),
            TenantId              = tenantId,
            AgencyId              = agencyId,
            Name                  = name,
            FirstContactDeadline  = firstContactDeadline,
            QualificationDeadline = qualificationDeadline,
            FollowUpDeadline      = followUpDeadline,
            EscalationDeadline    = escalationDeadline,
            IsActive              = false,
            CreatedAt             = DateTimeOffset.UtcNow
        };

    public void Update(
        string name,
        TimeSpan firstContactDeadline,
        TimeSpan qualificationDeadline,
        TimeSpan followUpDeadline,
        TimeSpan escalationDeadline)
    {
        Name                  = name;
        FirstContactDeadline  = firstContactDeadline;
        QualificationDeadline = qualificationDeadline;
        FollowUpDeadline      = followUpDeadline;
        EscalationDeadline    = escalationDeadline;
    }

    public void Activate()   => IsActive = true;
    public void Deactivate() => IsActive = false;
}
