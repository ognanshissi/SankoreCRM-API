namespace Sankore.Modules.Administration.Domain;

public class UserRole
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public AppRole? Role { get; private set; }
    public AppUser? User { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public Guid AssignedBy { get; private set; }
    public bool IsActive { get; private set; } = true;

    private UserRole() { }

    public static UserRole Assign(Guid tenantId, Guid userId, Guid roleId, Guid assignedBy)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            RoleId = roleId,
            AssignedBy = assignedBy,
            AssignedAt = DateTimeOffset.UtcNow,
            IsActive = true
        };

    public void Revoke() => IsActive = false;
}