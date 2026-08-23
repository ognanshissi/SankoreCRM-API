using Microsoft.AspNetCore.Identity;
using Sankore.Modules.Administration.Domain.Events;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Internal entity of the Administration module. NEVER referenced outside this
/// assembly — other modules only ever see the AgentSummary DTO exposed via
/// Sankore.Modules.Administration.PublicApi.IAdministrationModule.
/// </summary>
public sealed class AppUser: IdentityUser<Guid>
{
    private const int MaxFailedLoginAttempts = 5;
    private const int PasswordExpiryDays = 90;

    // ── Identity & tenancy ────────────────────────────────────────────────
    public Guid TenantId { get; private set; }
    public Guid? AgencyId { get; private set; }
    public Agency? Agency { get; private set; } = null!;
    public string FullName { get; private set; } = null!;

    // ── M12 lifecycle (F12.1) ─────────────────────────────────────────────
    public UserStatus Status { get; private set; } = UserStatus.PendingActivation;
    public bool MfaEnabled { get; private set; } = true;
    public DateTimeOffset PasswordExpiresAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    
    public bool IsSuperUser { get; private set; }

    // ── Dispatching (M13) ─────────────────────────────────────────────────
    public bool IsAvailable { get; private set; } = true;
    public List<string> SpokenLanguages { get; private set; } = [];
    public List<string> Specialties { get; private set; } = [];
    public GeoPoint? LastKnownLocation { get; private set; }
    public Guid ReportToId { get; private set; }
    public int ActiveLeadsCount { get; private set; }
    public int HotLeadsCount { get; private set; }
    public double ConversionRate30D { get; private set; }
    public bool EnableNotifications { get; private set; }

    // ── Relations ─────────────────────────────────────────────────────────
    private readonly List<PermissionAttribution> _attributions = [];
    public IReadOnlyCollection<PermissionAttribution> PermissionAttributions => _attributions.AsReadOnly();
    
    private readonly List<UserRole> _userRoles = [];
    public IReadOnlyCollection<UserRole> UserRoles => _userRoles.AsReadOnly();
    public UserProfile? Profile { get; private set; }
    
    public UserAccountType  AccountType { get; private set; }

    private AppUser() { } // EF Core

    // ── Factories ─────────────────────────────────────────────────────────

    public static AppUser Create(Guid tenantId, Guid agencyId, string fullName, string email)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("User must have a name.");
        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("User must have an email.");

        return new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgencyId = agencyId,
            FullName = fullName,
            UserName = email,
            Email = email,
            Status = UserStatus.PendingActivation,
            MfaEnabled = true,
            PasswordExpiresAt = DateTimeOffset.UtcNow.AddDays(PasswordExpiryDays),
            FailedLoginAttempts = 0,
            IsSuperUser = false,
            AccountType = UserAccountType.Standard,
        };
    }

    public static AppUser CreateRoot(Guid tenantId, string fullName, string email)
    {
        return new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            FullName = fullName,
            UserName = email,
            Email = email,
            Status = UserStatus.Active,
            MfaEnabled = true,
            PasswordExpiresAt = DateTimeOffset.UtcNow.AddDays(PasswordExpiryDays),
            FailedLoginAttempts = 0,
            IsSuperUser = true,
            AccountType = UserAccountType.System,
        };
    }

    public static AppUser CreateAgent(
        Guid tenantId, Guid agencyId, string fullName, string email,
        IEnumerable<string> languages, IEnumerable<string> specialties)
    {
        var user = Create(tenantId, agencyId, fullName, email);
        user.SpokenLanguages = languages.ToList();
        user.Specialties = specialties.ToList();
        user.IsAvailable = true;
        return user;
    }

    // ── Lifecycle behaviour (F12.1) ───────────────────────────────────────

    /// <summary>Transitions to Active (e.g. after first-login activation).</summary>
    public void Activate()
    {
        if (Status == UserStatus.Disabled)
            throw new DomainException("Cannot activate a disabled user.");
        Status = UserStatus.Active;
    }

    /// <summary>Logically deactivates the user. Returns the integration event to publish.</summary>
    public UserDeactivatedEvent Deactivate()
    {
        if (Status == UserStatus.Disabled)
            throw new DomainException("User is already disabled.");

        if (AccountType == UserAccountType.System) 
            throw new DomainException("Le compte système ne peut pas être désactivé.");
        
        Status = UserStatus.Disabled;
        DeactivatedAt = DateTimeOffset.UtcNow;
        IsAvailable = false;

        return new UserDeactivatedEvent(TenantId, Id);
    }

    /// <summary>Locks the account (too many failed attempts).</summary>
    public void Lock()
    {
        if (AccountType == UserAccountType.System) 
            throw new DomainException("The system is cannot be locked.");
        
        if (Status == UserStatus.Active || Status == UserStatus.PendingActivation)
            Status = UserStatus.Locked;
    }

    /// <summary>Records a successful login: resets failure counter and updates timestamp.</summary>
    public void RecordSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LastLoginAt = DateTimeOffset.UtcNow;
        if (Status == UserStatus.PendingActivation)
            Status = UserStatus.Active;
    }

    /// <summary>
    /// Increments the failed-login counter. Automatically locks the account when
    /// <see cref="MaxFailedLoginAttempts"/> is reached. Returns <c>true</c> if the
    /// account was just locked.
    /// </summary>
    public bool IncrementFailedLogin()
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts < MaxFailedLoginAttempts) return false;
        Lock();
        return true;
    }

    /// <summary>Extends the password expiry by <paramref name="days"/> days from now.</summary>
    public void ExtendPasswordExpiry(int days = PasswordExpiryDays)
        => PasswordExpiresAt = DateTimeOffset.UtcNow.AddDays(days);

    // ── Dispatching helpers (M13) ─────────────────────────────────────────

    public void UpdateLocation(GeoPoint location) => LastKnownLocation = location;

    public void SetAvailability(bool isAvailable) => IsAvailable = isAvailable;

    public void AdjustLeadCounters(int activeDelta, int hotDelta)
    {
        ActiveLeadsCount = Math.Max(0, ActiveLeadsCount + activeDelta);
        HotLeadsCount = Math.Max(0, HotLeadsCount + hotDelta);
    }
}

public enum UserAccountType
{
    Standard, // caissier, agent, superviseur
    System, // compte technique, un seul par tenant
    Service, // réservé si tu ajoutes plus tard des comptes API/intégration
}
