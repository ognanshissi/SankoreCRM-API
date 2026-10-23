namespace Sankore.Admin.Domain;

public enum ModuleSubscriptionStatus
{
    /// <summary>Asked for by the tenant, waiting for the platform.</summary>
    Requested,

    /// <summary>In force: the module is on for the tenant, and billed.</summary>
    Active,

    /// <summary>Refused by the platform, with a reason the tenant reads.</summary>
    Rejected,

    /// <summary>Withdrawn by the tenant before any decision.</summary>
    Cancelled,

    /// <summary>Switched off by the tenant. Immediate; no data is touched.</summary>
    Deactivated,
}

public enum ModuleSubscriptionSource
{
    /// <summary>A tenant administrator asked and the platform authorised.</summary>
    Request,

    /// <summary>The platform activated it without a request: a sale, a creation profile.</summary>
    Direct,

    /// <summary>Granted to a tenant that existed before modules did, so nothing changed for it.</summary>
    Migration,
}

/// <summary>
/// One module's history for one tenant, from the request (or a direct activation) to the end.
///
/// <para>
/// <b>A row is never deleted and never reused.</b> Asking again after a refusal, a cancellation or
/// a deactivation creates a NEW row, so the activation and deactivation dates of every period
/// stay readable: they are the billing basis.
/// </para>
///
/// <para>
/// At most one row per (tenant, module) is OPEN — <see cref="ModuleSubscriptionStatus.Requested"/>
/// or <see cref="ModuleSubscriptionStatus.Active"/>. The filtered unique index
/// <c>ux_tenant_module_open</c> guarantees it, not the read the handlers do first: two clicks on
/// « Demander » arrive together, and only the index sees both.
/// </para>
///
/// <para>
/// Transitions answer <c>false</c> when the row is not in the state they start from; the handler
/// turns that into a conflict. They never throw — this host maps no exception to a response.
/// </para>
/// </summary>
public sealed class TenantModuleSubscription
{
    public const int EmailMaxLength = 256;
    public const int ReasonMaxLength = 1000;

    public Guid Id { get; private set; }

    /// <summary>Logical reference to <see cref="Tenant"/>; no physical FK, like TenantDomains.</summary>
    public Guid TenantId { get; private set; }

    public string ModuleCode { get; private set; } = null!;
    public ModuleSubscriptionStatus Status { get; private set; }
    public ModuleSubscriptionSource Source { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RequestedAt { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public string? RequestedByEmail { get; private set; }

    /// <summary>The price the tenant accepted, copied from the catalogue at request time.</summary>
    public decimal? AcceptedMonthlyPrice { get; private set; }

    public string? AcceptedCurrency { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    /// <summary>The platform administrator who authorised, refused or activated directly.</summary>
    public Guid? DecidedByAdminId { get; private set; }

    public string? DecisionReason { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    /// <summary>Start of the billed period.</summary>
    public DateTimeOffset? ActivatedAt { get; private set; }

    /// <summary>End of the billed period.</summary>
    public DateTimeOffset? DeactivatedAt { get; private set; }

    public Guid? DeactivatedByUserId { get; private set; }
    public string? DeactivatedByEmail { get; private set; }

    public bool IsOpen => Status is ModuleSubscriptionStatus.Requested or ModuleSubscriptionStatus.Active;

    private TenantModuleSubscription() { }

    public static TenantModuleSubscription Request(
        Guid tenantId, string moduleCode, Guid userId, string email,
        decimal acceptedMonthlyPrice, string acceptedCurrency, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ModuleCode = moduleCode,
        Status = ModuleSubscriptionStatus.Requested,
        Source = ModuleSubscriptionSource.Request,
        CreatedAt = now,
        RequestedAt = now,
        RequestedByUserId = userId,
        RequestedByEmail = email,
        AcceptedMonthlyPrice = acceptedMonthlyPrice,
        AcceptedCurrency = acceptedCurrency.Trim().ToUpperInvariant(),
    };

    /// <summary>
    /// A module switched on without a request. <paramref name="adminId"/> is null when no operator
    /// is behind it — the migration, or a tenant created through the service key.
    /// </summary>
    public static TenantModuleSubscription ActivateDirect(
        Guid tenantId, string moduleCode, Guid? adminId, ModuleSubscriptionSource source, DateTimeOffset now)
    {
        if (source == ModuleSubscriptionSource.Request)
            throw new ArgumentException("A direct activation cannot claim to come from a request.", nameof(source));

        return new TenantModuleSubscription
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ModuleCode = moduleCode,
            Status = ModuleSubscriptionStatus.Active,
            Source = source,
            CreatedAt = now,
            DecidedAt = now,
            DecidedByAdminId = adminId,
            ActivatedAt = now,
        };
    }

    public bool Approve(Guid adminId, DateTimeOffset now)
    {
        if (Status != ModuleSubscriptionStatus.Requested) return false;

        Status = ModuleSubscriptionStatus.Active;
        DecidedAt = now;
        DecidedByAdminId = adminId;
        ActivatedAt = now;
        return true;
    }

    public bool Reject(Guid adminId, string reason, DateTimeOffset now)
    {
        if (Status != ModuleSubscriptionStatus.Requested || string.IsNullOrWhiteSpace(reason)) return false;

        Status = ModuleSubscriptionStatus.Rejected;
        DecidedAt = now;
        DecidedByAdminId = adminId;
        DecisionReason = reason.Trim();
        return true;
    }

    public bool Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status != ModuleSubscriptionStatus.Requested) return false;

        Status = ModuleSubscriptionStatus.Cancelled;
        CancelledAt = now;
        CancelledByUserId = userId;
        return true;
    }

    public bool Deactivate(Guid userId, string email, DateTimeOffset now)
    {
        if (Status != ModuleSubscriptionStatus.Active) return false;

        Status = ModuleSubscriptionStatus.Deactivated;
        DeactivatedAt = now;
        DeactivatedByUserId = userId;
        DeactivatedByEmail = email;
        return true;
    }
}
