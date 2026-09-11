namespace Sankore.Admin.Domain;

public class TenantDomain
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    public string Fqdn { get; private set; } = string.Empty;
    public bool IsPrimary { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset ValidTo { get; private set; }
    
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}