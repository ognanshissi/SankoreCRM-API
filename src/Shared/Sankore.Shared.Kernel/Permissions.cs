namespace Sankore.Shared.Kernel;


public record PermissionItem (string Code, string Description, string Module,  string Action);

public static class Permissions
{
    public static readonly PermissionItem CanCreateLoan = new PermissionItem("loan:create","Create Loan",ApplicationModules.Loan,"create");

    public static readonly PermissionItem CanCreateUser =
        new("user:create", "Create User", ApplicationModules.Administration, "create");

    public static readonly PermissionItem CanReadUser =
        new("user:read", "Read User", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanDeactivateUser =
        new("user:deactivate", "Deactivate User", ApplicationModules.Administration, "deactivate");

    public static readonly PermissionItem CanResetPassword =
        new("user:reset-password", "Reset User Password", ApplicationModules.Administration, "reset-password");

    public static readonly PermissionItem CanCreateAgency =
        new("agency:create", "Create Agency", ApplicationModules.Administration, "create");

    public static readonly PermissionItem CanReadAgency =
        new("agency:read", "Read Agency", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanUpdateAgency =
        new("agency:update", "Update Agency", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanDeleteAgency =
        new("agency:delete", "Delete Agency", ApplicationModules.Administration, "delete");

    public static readonly PermissionItem CanActivateAgency =
        new("agency:activate", "Activate Agency", ApplicationModules.Administration, "activate");

    public static readonly PermissionItem CanMoveAgency =
        new("agency:move", "Move Agency", ApplicationModules.Administration, "move");

    public static readonly PermissionItem CanCreateTerritory =
        new("territory:create", "Create Territory", ApplicationModules.Administration, "create");

    public static readonly PermissionItem CanReadTerritory =
        new("territory:read", "Read Territory", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanUpdateTerritory =
        new("territory:update", "Update Territory", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanDeleteTerritory =
        new("territory:delete", "Delete Territory", ApplicationModules.Administration, "delete");

    public static readonly PermissionItem CanReadAudit =
        new("audit:read", "Read Audit Trail", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanAssignRole =
        new("user:assign-role", "Assign Role to User", ApplicationModules.Administration, "assign-role");

    public static readonly PermissionItem CanRevokeRole =
        new("user:revoke-role", "Revoke Role from User", ApplicationModules.Administration, "revoke-role");

    public static readonly PermissionItem CanAssignPermission =
        new("user:assign-permission", "Assign Scoped Permission to User", ApplicationModules.Administration, "assign-permission");

    public static readonly PermissionItem CanRevokePermission =
        new("user:revoke-permission", "Revoke Scoped Permission from User", ApplicationModules.Administration, "revoke-permission");

    // ── Workflow module ────────────────────────────────────────────────────

    public static readonly PermissionItem CanCreateWorkflow =
        new("workflow:create", "Create Workflow Template", ApplicationModules.Workflow, "create");

    public static readonly PermissionItem CanReadWorkflow =
        new("workflow:read", "Read Workflow Template", ApplicationModules.Workflow, "read");

    public static readonly PermissionItem CanUpdateWorkflow =
        new("workflow:update", "Update Workflow Template", ApplicationModules.Workflow, "update");

    public static readonly PermissionItem CanDeleteWorkflow =
        new("workflow:delete", "Deactivate Workflow Template", ApplicationModules.Workflow, "delete");

    public static readonly PermissionItem CanActivateWorkflow =
        new("workflow:activate", "Activate Workflow Template", ApplicationModules.Workflow, "activate");

    public static readonly PermissionItem CanManageWorkflowSteps =
        new("workflow:manage-steps", "Add/Remove Steps in Workflow Template", ApplicationModules.Workflow, "manage-steps");

    public static readonly PermissionItem CanStartWorkflow =
        new("workflow:start", "Start a Workflow Instance", ApplicationModules.Workflow, "start");

    public static readonly PermissionItem CanApproveWorkflow =
        new("workflow:approve", "Approve or Reject a Workflow Step", ApplicationModules.Workflow, "approve");

    public static readonly PermissionItem CanCancelWorkflow =
        new("workflow:cancel", "Cancel a Workflow Instance", ApplicationModules.Workflow, "cancel");

    public static readonly PermissionItem[] All =
    [
        CanCreateLoan,
        CanCreateAgency,
        CanReadAgency,
        CanUpdateAgency,
        CanDeleteAgency,
        CanActivateAgency,
        CanMoveAgency,
        CanCreateUser,
        CanReadUser,
        CanDeactivateUser,
        CanResetPassword,
        CanCreateTerritory,
        CanReadTerritory,
        CanUpdateTerritory,
        CanDeleteTerritory,
        CanReadAudit,
        CanAssignRole,
        CanRevokeRole,
        CanAssignPermission,
        CanRevokePermission,
        CanCreateWorkflow,
        CanReadWorkflow,
        CanUpdateWorkflow,
        CanDeleteWorkflow,
        CanActivateWorkflow,
        CanManageWorkflowSteps,
        CanStartWorkflow,
        CanApproveWorkflow,
        CanCancelWorkflow,
    ];
}
