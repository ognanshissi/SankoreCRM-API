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

    public static readonly PermissionItem CanUpdateUser =
        new("user:update", "Update User", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanReactivateUser =
        new("user:reactivate", "Reactivate User", ApplicationModules.Administration, "reactivate");

    public static readonly PermissionItem CanAssignRole =
        new("user:assign-role", "Assign Role to User", ApplicationModules.Administration, "assign-role");

    public static readonly PermissionItem CanRevokeRole =
        new("user:revoke-role", "Revoke Role from User", ApplicationModules.Administration, "revoke-role");

    public static readonly PermissionItem CanAssignPermission =
        new("user:assign-permission", "Assign Scoped Permission to User", ApplicationModules.Administration, "assign-permission");

    public static readonly PermissionItem CanRevokePermission =
        new("user:revoke-permission", "Revoke Scoped Permission from User", ApplicationModules.Administration, "revoke-permission");

    // ── Role management (F12.2 RBAC) ──────────────────────────────────────

    public static readonly PermissionItem CanCreateRole =
        new("role:create", "Create Custom Role", ApplicationModules.Administration, "create");

    public static readonly PermissionItem CanReadRole =
        new("role:read", "Read Role Details", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanUpdateRole =
        new("role:update", "Update Custom Role", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanDeleteRole =
        new("role:delete", "Delete Custom Role", ApplicationModules.Administration, "delete");

    public static readonly PermissionItem CanManageRolePermissions =
        new("role:manage-permissions", "Assign/Revoke Permissions on a Role", ApplicationModules.Administration, "manage-permissions");

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

    public static readonly PermissionItem CanViewWorkflowInstances =
        new("workflow:instance:view", "List and View Workflow Instances", ApplicationModules.Workflow, "view");

    public static readonly PermissionItem CanCompleteWorkflowTask =
        new("workflow:task:complete", "Complete or Cancel a Workflow Task", ApplicationModules.Workflow, "complete");

    public static readonly PermissionItem CanAssignWorkflowStep =
        new("workflow:step:assign", "Assign or Reassign a Workflow Step", ApplicationModules.Workflow, "assign");

    public static readonly PermissionItem CanManageWorkflowTriggers =
        new("workflow:trigger:manage", "Add, Remove and List Workflow Triggers", ApplicationModules.Workflow, "manage");

    public static readonly PermissionItem CanViewWorkflowAnalytics =
        new("workflow:analytics:view", "View Workflow Analytics and Statistics", ApplicationModules.Workflow, "view");

    // ── Product catalogue (F12.4) ─────────────────────────────────────────

    public static readonly PermissionItem CanCreateProduct =
        new("product:create", "Create Product Speciality", ApplicationModules.Administration, "create");

    public static readonly PermissionItem CanReadProduct =
        new("product:read", "Read Product Speciality", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanUpdateProduct =
        new("product:update", "Update Product Speciality", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanDeleteProduct =
        new("product:delete", "Delete Product Speciality", ApplicationModules.Administration, "delete");

    // ── Leads module (M13) ────────────────────────────────────────────────

    public static readonly PermissionItem CanCaptureLead =
        new("lead:create", "Capture / Create a Lead", ApplicationModules.Leads, "create");

    public static readonly PermissionItem CanReadLead =
        new("lead:read", "View Lead Details", ApplicationModules.Leads, "read");

    public static readonly PermissionItem CanUpdateLead =
        new("lead:update", "Update Lead Information", ApplicationModules.Leads, "update");

    public static readonly PermissionItem CanAssignLead =
        new("lead:assign", "Assign / Reassign a Lead Owner", ApplicationModules.Leads, "assign");

    public static readonly PermissionItem CanQualifyLead =
        new("lead:qualify", "Qualify a Lead", ApplicationModules.Leads, "qualify");

    public static readonly PermissionItem CanCloseLead =
        new("lead:close", "Mark a Lead as Lost, Disqualified or Archived", ApplicationModules.Leads, "close");

    public static readonly PermissionItem CanMovePipelineStage =
        new("lead:pipeline", "Move a Lead Along the Sales Pipeline", ApplicationModules.Leads, "pipeline");

    public static readonly PermissionItem CanLogLeadActivity =
        new("lead:activity:log", "Log an Activity on a Lead", ApplicationModules.Leads, "activity:log");

    public static readonly PermissionItem CanConvertLead =
        new("lead:convert", "Convert a Lead into a Customer", ApplicationModules.Leads, "convert");

    public static readonly PermissionItem CanNurtureLead =
        new("lead:nurture", "Move a Lead into the Nurturing State", ApplicationModules.Leads, "nurture");

    public static readonly PermissionItem CanRecycleLead =
        new("lead:recycle", "Recycle a Closed or Nurturing Lead", ApplicationModules.Leads, "recycle");

    public static readonly PermissionItem CanManageDispatchingRules =
        new("lead:dispatching-rule:manage", "Create and Update Dispatching Rules", ApplicationModules.Leads, "dispatching-rule:manage");

    public static readonly PermissionItem CanReadDispatchingRules =
        new("lead:dispatching-rule:read", "Read Dispatching Rules", ApplicationModules.Leads, "dispatching-rule:read");

    public static readonly PermissionItem CanManageLeadReminders =
        new("lead:reminder:manage", "Create, Complete and Dismiss Lead Reminders", ApplicationModules.Leads, "reminder:manage");

    public static readonly PermissionItem CanTagLead =
        new("lead:tag", "Add and Remove Tags on a Lead", ApplicationModules.Leads, "tag");

    public static readonly PermissionItem CanImportLeads =
        new("lead:import", "Bulk-import Leads from a structured list", ApplicationModules.Leads, "import");

    public static readonly PermissionItem CanMergeLeads =
        new("lead:merge", "Merge a Duplicate Lead into a Target Lead", ApplicationModules.Leads, "merge");

    public static readonly PermissionItem CanDismissDuplicate =
        new("lead:duplicate:dismiss", "Dismiss a Potential Duplicate Match", ApplicationModules.Leads, "duplicate:dismiss");

    public static readonly PermissionItem CanExportLeads =
        new("lead:export", "Export Leads to CSV", ApplicationModules.Leads, "export");

    public static readonly PermissionItem CanViewLeadAnalytics =
        new("lead:analytics:view", "View Lead SLA, Funnel and Agent Performance Analytics", ApplicationModules.Leads, "analytics:view");

    // ── Company info (Administration module) ──────────────────────────────

    public static readonly PermissionItem CanReadCompanyInfo =
        new("company:read", "Read Company Info", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanUpdateCompanyInfo =
        new("company:update", "Update Company Info", ApplicationModules.Administration, "update");

    // ── Notification settings (Administration module) ─────────────────────

    public static readonly PermissionItem CanReadNotificationSettings =
        new("notification:settings:read", "Read Tenant Notification Settings", ApplicationModules.Administration, "read");

    public static readonly PermissionItem CanManageNotificationSettings =
        new("notification:settings:manage", "Configure Tenant Email Provider", ApplicationModules.Administration, "manage");

    public static readonly PermissionItem CanManageEmailQuota =
        new("notification:settings:quota", "Set Monthly Email Quota", ApplicationModules.Administration, "quota");

    // ── Notifications module ───────────────────────────────────────────────

    public static readonly PermissionItem CanReadEmailTemplates =
        new("notification:template:read", "Read Email Templates", ApplicationModules.Notifications, "read");

    public static readonly PermissionItem CanManageEmailTemplates =
        new("notification:template:manage", "Create / Update Email Templates", ApplicationModules.Notifications, "manage");

    public static readonly PermissionItem CanReadEmailOutbox =
        new("notification:outbox:read", "Read Email Outbox", ApplicationModules.Notifications, "read");

    public static readonly PermissionItem CanRetryEmailOutbox =
        new("notification:outbox:retry", "Retry Dead-lettered Emails", ApplicationModules.Notifications, "retry");

    public static readonly PermissionItem CanReadEmailDeliveryLogs =
        new("notification:delivery-log:read", "Read Email Delivery Logs", ApplicationModules.Notifications, "read");

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
        CanUpdateUser,
        CanDeactivateUser,
        CanReactivateUser,
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
        CanCreateRole,
        CanReadRole,
        CanUpdateRole,
        CanDeleteRole,
        CanManageRolePermissions,
        CanCreateProduct,
        CanReadProduct,
        CanUpdateProduct,
        CanDeleteProduct,
        CanCreateWorkflow,
        CanReadWorkflow,
        CanUpdateWorkflow,
        CanDeleteWorkflow,
        CanActivateWorkflow,
        CanManageWorkflowSteps,
        CanStartWorkflow,
        CanApproveWorkflow,
        CanCancelWorkflow,
        CanViewWorkflowInstances,
        CanCompleteWorkflowTask,
        CanAssignWorkflowStep,
        CanManageWorkflowTriggers,
        CanViewWorkflowAnalytics,
        CanReadEmailTemplates,
        CanManageEmailTemplates,
        CanReadEmailOutbox,
        CanRetryEmailOutbox,
        CanReadEmailDeliveryLogs,
        CanReadNotificationSettings,
        CanManageNotificationSettings,
        CanManageEmailQuota,
        CanReadCompanyInfo,
        CanUpdateCompanyInfo,
        CanCaptureLead,
        CanReadLead,
        CanUpdateLead,
        CanAssignLead,
        CanQualifyLead,
        CanCloseLead,
        CanMovePipelineStage,
        CanLogLeadActivity,
        CanConvertLead,
        CanNurtureLead,
        CanRecycleLead,
        CanManageDispatchingRules,
        CanReadDispatchingRules,
        CanManageLeadReminders,
        CanTagLead,
        CanImportLeads,
        CanMergeLeads,
        CanDismissDuplicate,
        CanExportLeads,
        CanViewLeadAnalytics,
    ];
}
