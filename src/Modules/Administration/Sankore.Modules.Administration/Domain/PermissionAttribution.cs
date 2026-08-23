namespace Sankore.Modules.Administration.Domain;

public class PermissionAttribution
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>
    /// Nullable — null means the permission applies globally (no scope restriction).
    /// </summary>
    public Guid? ScopeId { get; private set; }

    /// <summary>"Agency", "Territory", "Tenant", or null for global.</summary>
    public string? ScopeType { get; private set; }

    public Guid UserId { get; private set; }
    public AppUser User { get; private set; } = null!;

    public DateTimeOffset StartDate { get; private set; }
    public DateTimeOffset EndDate { get; private set; }
    public bool IsActive { get; private set; } = true;
    public Guid AssignedByUserId { get; private set; }

    public string PermissionCode { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private PermissionAttribution() { }

    public void Revoke()
    {
        IsActive = false;
        EndDate = DateTimeOffset.UtcNow;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public static PermissionAttribution Create(
        Guid tenantId,
        Guid userId,
        string permissionCode,
        Guid assignedByUserId,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        Guid? scopeId = null,
        string? scopeType = null)
    {
        return new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            PermissionCode = permissionCode,
            AssignedByUserId = assignedByUserId,
            StartDate = startDate,
            EndDate = endDate,
            ScopeId = scopeId,
            ScopeType = scopeType,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}
