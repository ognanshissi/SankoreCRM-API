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

    /// <summary>
    /// The user this one reports to, or null at the top of the line. Deliberately independent of
    /// <see cref="AgencyId"/>: a regional director in head office manages branch staff, so a
    /// reporting line that had to stay inside one agency would be unusable.
    ///
    /// No physical foreign key — it points at the same table, and a self-referencing FK turns
    /// every delete into a cascade question. The reference is validated when it is set.
    /// </summary>
    public Guid? ReportsToUserId { get; private set; }
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

    /// <summary>
    /// Arrivée dans l'institution. Nullable : les comptes antérieurs à la colonne ont été
    /// rétro-datés sur leur première connexion, et ceux qui ne s'étaient jamais connectés n'ont
    /// aucune date fiable — mieux vaut « inconnue » qu'une date de migration présentée comme vraie.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; private set; }

    /// <summary>Matricule interne (AGT-0001…), attribué à la création. L'utilisateur ne le modifie pas.</summary>
    public string? EmployeeNumber { get; private set; }

    /// <summary>Qui a créé le compte ; null pour un compte racine ou antérieur à la colonne.</summary>
    public Guid? CreatedByUserId { get; private set; }

    /// <summary>
    /// Dernier envoi d'un lien d'activation. Le lien vit <c>Identity:ActivationTokenLifespan</c>
    /// à partir de là, et chaque nouvel envoi invalide les précédents.
    /// </summary>
    public DateTimeOffset? InvitationSentAt { get; private set; }

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

    /// <summary>
    /// Stores the code normalised — "FR", "Fr" and "fr-FR" are one language, not three. Blank
    /// stays null, because "no preference" is meaningful here: ModuleEmailSender then falls back
    /// to the tenant's default rather than assuming French.
    /// </summary>
    public void SetPreferredLanguage(string? language)
        => PreferredLanguage = LanguageCode.NormalizeOrNull(language);

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
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Creates the tenant's super-user. Takes the name in two parts like
    /// <see cref="Create"/>: FirstName and LastName are non-nullable and EF rejects the insert
    /// when they are missing, so a root built from a single display string could never be saved.
    /// </summary>
    public static AppUser CreateRoot(Guid tenantId, string firstName, string lastName, string email)
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
            FirstName = firstName.Trim(),
            LastName = lastName.Trim(),
            FullName = $"{firstName.Trim()} {lastName.Trim()}",
            UserName = email,
            Email = email,
            Status = UserStatus.Active,
            MfaEnabled = true,
            PasswordExpiresAt = DateTimeOffset.UtcNow.AddDays(PasswordExpiryDays),
            FailedLoginAttempts = 0,
            IsSuperUser = true,
            AccountType = UserAccountType.System,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public static AppUser CreateAgent(
        Guid tenantId, Guid agencyId, string firstName, string lastName, string email,
        IEnumerable<string> languages, IEnumerable<string> specialties)
    {
        var user = Create(tenantId, agencyId, firstName, lastName, email);
        user.SpokenLanguages = NormalizeLanguages(languages);
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
        if (spokenLanguages is not null) SpokenLanguages = NormalizeLanguages(spokenLanguages);
        if (specialties is not null) Specialties = specialties;
        if (enableNotifications is not null) EnableNotifications = enableNotifications.Value;
    }

    /// <summary>
    /// Ce que l'utilisateur corrige lui-même sur son identité. Le nom complet suit : c'est lui que
    /// lisent les listes, les e-mails et les autres modules.
    /// </summary>
    public void RenameSelf(string firstName, string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            throw new DomainException("Le prénom et le nom sont obligatoires.", "User.Name.Required");

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        FullName = $"{FirstName} {LastName}";
    }

    public void RecordCreator(Guid creatorUserId) => CreatedByUserId ??= creatorUserId;

    public void MarkInvitationSent(DateTimeOffset at) => InvitationSentAt = at;

    /// <summary>
    /// Change l'adresse — et donc l'identifiant de connexion — d'un compte pas encore activé.
    /// Refusé ensuite : un compte actif qui change d'adresse doit prouver qu'il la possède, et ce
    /// circuit de confirmation n'existe pas. Avant activation, personne ne s'est encore connecté
    /// avec l'ancienne : la nouvelle invitation suffit à la valider.
    /// </summary>
    public void ChangeEmailBeforeActivation(string email)
    {
        if (Status != UserStatus.PendingActivation)
            throw new DomainException(
                "L'adresse ne peut être modifiée qu'avant l'activation du compte.", "User.Email.LockedAfterActivation");
        if (string.IsNullOrWhiteSpace(email))
            throw new DomainException("User must have an email.", "User.Email.Required");

        var trimmed = email.Trim();
        Email = trimmed;
        UserName = trimmed;
        NormalizedEmail = trimmed.ToUpperInvariant();
        NormalizedUserName = trimmed.ToUpperInvariant();
        EmailConfirmed = false;
    }

    /// <summary>Blanc efface le matricule.</summary>
    public void SetEmployeeNumber(string? employeeNumber)
        => EmployeeNumber = string.IsNullOrWhiteSpace(employeeNumber) ? null : employeeNumber.Trim();

    /// <summary>
    /// Sets who this user reports to. The caller must have checked that the manager exists, is
    /// active, and does not already report to this user — the aggregate cannot see other users,
    /// and a reporting line is a graph invariant, not a local one.
    /// </summary>
    public void ReportTo(Guid managerUserId)
    {
        if (managerUserId == Guid.Empty)
            throw new DomainException("A manager user id is required.", "User.Manager.Required");
        if (managerUserId == Id)
            throw new DomainException("A user cannot report to themselves.", "User.Manager.Self");

        ReportsToUserId = managerUserId;
    }

    /// <summary>Puts this user at the top of their line. A no-op when there is no manager.</summary>
    public void ClearReportingLine() => ReportsToUserId = null;

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

    /// <summary>
    /// Normalises each code and drops duplicates that only differed by case or subtag, keeping
    /// the first occurrence so a deliberate ordering survives.
    /// </summary>
    private static List<string> NormalizeLanguages(IEnumerable<string> languages)
        => languages
            .Select(LanguageCode.NormalizeOrNull)
            .Where(l => l is not null)
            .Select(l => l!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

public enum UserAccountType
{
    Standard, // caissier, agent, superviseur
    System, // compte technique, un seul par tenant
    Service, // réservé si tu ajoutes plus tard des comptes API/intégration
}
