namespace Sankore.Modules.Administration.Domain;

public class PermissionAttribution
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid? ScopeId { get; private set; } //  can be null
    public string? ScopeType { get; private set; } = null!; // Agency, territory, Tenant null = global
    public Guid UserId { get; private set; }
    public AppUser User { get; private set; } = null!;
    
    public DateTimeOffset StartDate { get; private set; }
    public DateTimeOffset EndDate { get; private set; }
    public bool IsActive { get; private set; } = true;
    public Guid AssignedByUserId { get; private set; }

    public string PermissionCode { get; private set; } = null!; // ex: "AGENCY:SUPERVISION", "LEAD:REASSIGN",  "SYSTEM:ALL"
    
    public DateTimeOffset CreateAt { get; private set; }
    public DateTimeOffset UpdateAt { get; private set; }
    
    private PermissionAttribution() {}
    
    public void Revoke(Guid revokedByUserId)
    {
        IsActive = false;
        EndDate = DateTime.UtcNow;
    }

    public static PermissionAttribution Create(Guid tenantId, Guid scopeId, string scopeType, Guid userId, string permissionCode, DateTimeOffset startDate)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ScopeId = scopeId,
            ScopeType = scopeType,
            PermissionCode = permissionCode,
            UserId = userId,
            StartDate = startDate
        };
    }
}