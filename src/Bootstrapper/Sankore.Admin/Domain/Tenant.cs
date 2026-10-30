namespace Sankore.Admin.Domain;

public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string RootUserEmail { get; private set; } = null!;
    public string Fqdn { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public DateTimeOffset? TrialExpiresAt { get; private set; }
    public bool IsMaintenance { get; private set; }

    /// <summary>
    /// A recette or demo institution (data-purge lot 4): the only kind whose business data the
    /// platform may wipe. Set by a platform administrator, never by the tenant.
    /// </summary>
    public bool IsTest { get; private set; }

    /// <summary>
    /// Set on a TEST TWIN only (sandbox lot 2): the live tenant whose « Mode test » this is. A twin
    /// shares its parent's users, modules and state and keeps its own data and configuration; it has
    /// no address of its own (see <see cref="TwinFqdnSuffix"/>) and is reached by switching mode
    /// inside the parent, never by signing in to it.
    /// </summary>
    public Guid? ParentTenantId { get; private set; }

    /// <summary>
    /// Appended to the parent's FQDN to fill the twin's unique column. « ~ » cannot appear in a
    /// host name, so no request can ever resolve a twin by its address.
    /// </summary>
    public const string TwinFqdnSuffix = "~test";
    public DateTimeOffset? BlockedAt { get; private set; }
    
    public string BlockedReason { get; private set; } = null!;
    
    public SubscriptionState  SubscriptionState { get; set; } = SubscriptionState.Active;
    
    public SubscriptionStateInfo? SubscriptionStateInfo { get; set; }
    
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Tenant() { }

    public static Tenant Create(string name, string rootUserEmail, string fqdn,  DateTimeOffset? trialExpiresAt = null)
    {
        return new Tenant
        {
            Id = Guid.NewGuid(),
            Name = name,
            RootUserEmail = rootUserEmail,
            Fqdn = fqdn,
            IsActive = true,
            TrialExpiresAt = trialExpiresAt,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            BlockedReason = string.Empty,
            SubscriptionState = SubscriptionState.Active,
            SubscriptionStateInfo = new SubscriptionStateInfo(DateTimeOffset.UtcNow.AddYears(1), RenewalPeriod.Yearly)
        };
    }

    /// <summary>The test twin of <paramref name="parent"/>: always a test tenant, active, no trial.</summary>
    public static Tenant CreateTestTwin(Tenant parent)
    {
        if (parent.ParentTenantId is not null)
            throw new InvalidOperationException("A test twin has no twin of its own.");

        // Both columns are unique: the twin carries the parent's values made unusable, so it can
        // never be signed in to nor resolved on its own.
        var twin = Create($"{parent.Name} (test)", parent.RootUserEmail + TwinFqdnSuffix, parent.Fqdn + TwinFqdnSuffix);
        twin.ParentTenantId = parent.Id;
        twin.IsTest = true;
        return twin;
    }

    public void Update(string name, string fqdn)
    {
        Name = name;
        Fqdn = fqdn;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        BlockedAt = null;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        BlockedAt = DateTimeOffset.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkAsTest(bool isTest)
    {
        IsTest = isTest;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetMaintenance(bool value)
    {
        IsMaintenance = value;
        UpdatedAt = DateTime.UtcNow;
    }
}