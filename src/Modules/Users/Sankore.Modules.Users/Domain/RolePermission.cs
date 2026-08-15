namespace Sankore.Modules.Users.Domain;

public class RolePermission
{
    public Guid Id { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public DateTimeOffset GrantedAt { get; private set; }

    // Navigation properties
    public AppRole Role { get; private set; } = default!;
    public Permission Permission { get; private set; } = default!;

    private RolePermission() { }

    public static RolePermission Grant(Guid roleId, Guid permissionId) => new()
    {
        Id = Guid.NewGuid(),
        RoleId = roleId,
        PermissionId = permissionId,
        GrantedAt = DateTimeOffset.UtcNow
    };
}
