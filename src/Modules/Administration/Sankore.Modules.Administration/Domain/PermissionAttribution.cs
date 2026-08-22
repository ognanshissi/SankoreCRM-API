namespace Sankore.Modules.Administration.Domain;

public class PermissionAttribution
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid EntityId { get; private set; } //  can be null
    public string EntityName { get; private set; } = null!; // Agency, territory
    public Guid UserId { get; private set; }
    public AppUser User { get; private set; } = null!;
    
    public DateTimeOffset StartDate { get; private set; }
    public DateTimeOffset EndDate { get; private set; }
    public bool IsActive { get; private set; } = true;
    public Guid AssignedByUserId { get; private set; }

    public string PermissionCode { get; private set; } = null!;
    
    public DateTimeOffset CreateAt { get; private set; }
    public DateTimeOffset UpdateAt { get; private set; }
    
    private PermissionAttribution() {}
    
    public void Revoke(Guid revokedByUserId)
    {
        IsActive = false;
        EndDate = DateTime.UtcNow;
    }

    public static PermissionAttribution Create(Guid tenantId, Guid entityId, string entityName, Guid userId, string permissionCode, DateTimeOffset startDate)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            EntityId = entityId,
            EntityName = entityName,
            PermissionCode = permissionCode,
            UserId = userId,
            StartDate = startDate
        };
    }
}