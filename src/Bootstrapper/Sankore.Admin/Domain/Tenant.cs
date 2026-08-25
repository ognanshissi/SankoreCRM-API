namespace Sankore.Admin.Domain;

public class Tenant
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public string RootUserEmail { get; private set; } = null!;
    public bool IsActive { get;private set; }
    public DateTimeOffset? TrialExpiresAt { get; private set; } = null!; // Not trial
    public bool IsMaintenance { get; private set; }
    public DateTimeOffset BlockedAt { get; private set; } // Can be blocked if future
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
}