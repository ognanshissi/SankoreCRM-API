namespace Sankore.Modules.Users.Domain;

using Sankore.Shared.Kernel;

public enum UserRole
{
    Cashier,
    CommercialAgent,
    BranchManager,
    SalesManager,
    ComplianceOfficer,
    Administrator
}

/// <summary>
/// Internal entity of the Users module. NEVER referenced outside this
/// assembly — other modules only ever see the AgentSummary DTO exposed via
/// Sankore.Modules.Users.PublicApi.IUsersModule.
/// </summary>
public sealed class AppUser : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AgencyId { get; private set; }
    public string FullName { get; private set; } = default!;
    public string Email { get; private set; } = default!;
    public UserRole Role { get; private set; }
    public bool IsAvailable { get; private set; } = true;

    public List<string> SpokenLanguages { get; private set; } = new();
    public List<string> Specialties { get; private set; } = new();
    public GeoPoint? LastKnownLocation { get; private set; }

    public int ActiveLeadsCount { get; private set; }
    public int HotLeadsCount { get; private set; }
    public double ConversionRate30d { get; private set; }

    private AppUser() { } // EF Core

    public static AppUser CreateAgent(
        Guid tenantId, Guid agencyId, string fullName, string email,
        IEnumerable<string> languages, IEnumerable<string> specialties)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new DomainException("Agent must have a name.");

        return new AppUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            AgencyId = agencyId,
            FullName = fullName,
            Email = email,
            Role = UserRole.CommercialAgent,
            SpokenLanguages = languages.ToList(),
            Specialties = specialties.ToList(),
            IsAvailable = true
        };
    }

    public void UpdateLocation(GeoPoint location) => LastKnownLocation = location;

    public void SetAvailability(bool isAvailable) => IsAvailable = isAvailable;

    /// <summary>Called (via an event handler) whenever a lead is assigned/unassigned to this agent.</summary>
    public void AdjustLeadCounters(int activeDelta, int hotDelta)
    {
        ActiveLeadsCount = Math.Max(0, ActiveLeadsCount + activeDelta);
        HotLeadsCount = Math.Max(0, HotLeadsCount + hotDelta);
    }
}
