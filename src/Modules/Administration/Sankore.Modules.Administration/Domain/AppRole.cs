using Microsoft.AspNetCore.Identity;

namespace Sankore.Modules.Administration.Domain;

/// <summary>
/// Custom role entity. Extends IdentityRole to carry IsSystem so built-in
/// (seeded) roles can be distinguished from tenant-created ones.
/// </summary>
public class AppRole : IdentityRole<Guid>
{
    public Guid? TenantId { get; private set; } // System role doesn't require tenantId
    
    public bool IsSystem { get; private set; }
    public string Label { get; private set; } = string.Empty;

    private AppRole() { }

    public static AppRole Create(string name, string label, bool isSystem = false) => new()
    {
        Name = name,
        Label = label,
        IsSystem = isSystem
    };
}