namespace Sankore.Modules.Users.Domain;

public class UserRole
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public AppRole? Role { get; private set; } = null;
    public AppUser? User { get; private set; } = null;
    
    public DateTimeOffset AssignedAt { get; private set; } = DateTimeOffset.Now;
    public Guid AssignedBy { get; private set; }
    public bool IsActive { get; private set; } = true;
    
    public void Revoked() =>  IsActive = false;
}