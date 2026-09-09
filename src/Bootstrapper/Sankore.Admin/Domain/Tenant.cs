namespace Sankore.Admin.Domain;

public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string RootUserEmail { get; private set; } = null!;
    public string Fqdn { get; private set; } = null!;
    public bool IsActive { get; private set; }

    public List<ApplicationModules> ApplicationModulesList = [];
    
    public DateTimeOffset? TrialExpiresAt { get; private set; }
    public bool IsMaintenance { get; private set; }
    public DateTimeOffset? BlockedAt { get; private set; }

    public string BlockedReason { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    private Tenant() { }

    public static Tenant Create(string name, string rootUserEmail, string fqdn, DateTimeOffset? trialExpiresAt = null)
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
            UpdatedAt = DateTime.UtcNow
        };
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

    public void SetMaintenance(bool value)
    {
        IsMaintenance = value;
        UpdatedAt = DateTime.UtcNow;
    }
}