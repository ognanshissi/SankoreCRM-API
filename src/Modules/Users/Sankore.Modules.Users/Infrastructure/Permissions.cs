using Sankore.Shared.Kernel;

namespace Sankore.Modules.Users.Infrastructure;


public record PermissionItem (string Code, string Description, string Module,  string Action);

public static class Permissions
{
    public static readonly PermissionItem CanCreateLoan = new PermissionItem("loan:create","Create Loan",ApplicationModules.Loan,"create");
    
    public static readonly PermissionItem CanCreateUser =
        new PermissionItem("user:create", "Create User", ApplicationModules.Identity, "create");
    
    
    public static readonly PermissionItem[] All =
    [
        CanCreateLoan,
        CanCreateUser
    ];
}