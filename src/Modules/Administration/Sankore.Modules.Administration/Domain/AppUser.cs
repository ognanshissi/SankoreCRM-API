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

    public string FirstName { get; private set; } = null!;
    public string LastName { get; private set; } = null!;

    // ── M12 lifecycle (F12.1) ─────────────────────────────────────────────
    public UserStatus Status { get; private set; } = UserStatus.PendingActivation;
    public bool MfaEnabled { get; private set; } = true;
    public DateTimeOffset PasswordExpiresAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset? LastLogoutAt { get; private set; }
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

    public UserAccountType AccountType { get; private set; }

    /// <summary>
    /// User's preferred UI language (BCP-47 tag, e.g. "fr", "en").
    /// Null means fall back to the tenant's DefaultLanguage.
    /// Emitted as the "lang" JWT claim so LanguageResolutionMiddleware
    /// picks it up at priority 1 on every subsequent request.
    /// </summary>
    public string? PreferredLanguage { get; private set; }

    public void SetPreferredLanguage(string? language) => PreferredLanguage = language;

    private AppUser() { } // EF Core

    // ── Factories ─────────────────────────────────────────────────────────

    public static AppUser Create(Guid tenantId, Guid agencyId, string firstName, string lastName, string email)
    {
        if (string.IsNullOrWhiteSpace(firstName))
            throw new DomainException("User must have a name.", "User.Name.Required");
        if (string.IsNullOrWhiteSpace(lastName))
            throw new DomainException("User must have a name.", "User.Name.Required");
        
        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("User must have an email.", "User.Email.Required");

        return new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgencyId = agencyId,
            FirstName = firstName,
            LastName = lastName,
            FullName = $"{firstName} {lastName}",
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
        Guid tenantId, Guid agencyId, string firstName, string lastName, string email,
        IEnumerable<string> languages, IEnumerable<string> specialties)
    {
        var user = Create(tenantId, agencyId, firstName, lastName, email);
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
            throw new DomainException("Cannot activate a disabled user.", "User.Disabled.CannotActivate");
        Status = UserStatus.Active;
    }

    /// <summary>Logically deactivates the user. Returns the integration event to publish.</summary>
    public UserDeactivatedEvent Deactivate()
    {
        if (Status == UserStatus.Disabled)
            throw new DomainException("User is already disabled.", "User.AlreadyDisabled");

        if (AccountType == UserAccountType.System)
            throw new DomainException("The system account cannot be disabled.", "User.System.CannotDisable");

        Status = UserStatus.Disabled;
        DeactivatedAt = DateTimeOffset.UtcNow;
        IsAvailable = false;

        return new UserDeactivatedEvent(TenantId, Id);
    }

    /// <summary>Locks the account (too many failed attempts).</summary>
    public void Lock()
    {
        if (AccountType == UserAccountType.System)
            throw new DomainException("The system account cannot be locked.", "User.System.CannotLock");

        if (Status == UserStatus.Active || Status == UserStatus.PendingActivation)
            Status = UserStatus.Locked;
    }

    /// <summary>Records the logout timestamp for audit purposes.</summary>
    public void RecordLogout() => LastLogoutAt = DateTimeOffset.UtcNow;

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

    /// <summary>
    /// Marks the password as immediately expired so the user is forced to change it
    /// on next login. Used by admin-initiated password resets.
    /// </summary>
    public void ExpirePasswordNow()
        => PasswordExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

    /// <summary>Updates mutable user details. Only non-null parameters are applied.</summary>
    public void UpdateDetails(
        string? fullName,
        Guid? agencyId,
        List<string>? spokenLanguages,
        List<string>? specialties,
        bool? enableNotifications)
    {
        if (fullName is not null)
        {
            if (string.IsNullOrWhiteSpace(fullName))
                throw new DomainException("Full name cannot be blank.", "User.FullName.Blank");
            FullName = fullName;
        }
        if (agencyId is not null) AgencyId = agencyId;
        if (spokenLanguages is not null) SpokenLanguages = spokenLanguages;
        if (specialties is not null) Specialties = specialties;
        if (enableNotifications is not null) EnableNotifications = enableNotifications.Value;
    }

    /// <summary>Re-enables a previously disabled user.</summary>
    public void Reactivate()
    {
        if (Status != UserStatus.Disabled)
            throw new DomainException("Only disabled users can be reactivated.", "User.OnlyDisabled.CanReactivate");
        if (AccountType == UserAccountType.System)
            throw new DomainException("System account cannot be reactivated.", "User.System.CannotReactivate");

        Status = UserStatus.Active;
        DeactivatedAt = null;
        IsAvailable = true;
    }

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
