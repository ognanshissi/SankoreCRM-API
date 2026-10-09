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

    public static readonly PermissionItem CanAssignAgencyManager =
        new("agency:assign-manager", "Assign or Remove an Agency Manager", ApplicationModules.Administration, "assign-manager");

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

    /// <summary>
    /// Starts the copy of stored objects from the filesystem to the configured bucket.
    ///
    /// <para>
    /// The permission is NOT the whole guard, and cannot be: RoleSeeder grants every permission to
    /// the Administrator role, and this operation walks every tenant's objects. MigrateObjectsHandler
    /// additionally requires the System role. Both, on purpose — the permission is what a screen
    /// hides a button on, the role check is what refuses the call.
    /// </para>
    /// </summary>
    public static readonly PermissionItem CanMigrateObjectStorage =
        new("platform:storage:migrate", "Migrate Stored Objects",
            ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanUpdateUser =
        new("user:update", "Update User", ApplicationModules.Administration, "update");

    public static readonly PermissionItem CanReactivateUser =
        new("user:reactivate", "Reactivate User", ApplicationModules.Administration, "reactivate");

    public static readonly PermissionItem CanAssignUserManager =
        new("user:assign-manager", "Set or Clear a User's Reporting Line", ApplicationModules.Administration, "assign-manager");

    public static readonly PermissionItem CanAssignUserAgency =
        new("user:assign-agency", "Assign Users to an Agency", ApplicationModules.Administration, "assign-agency");

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

    public static readonly PermissionItem CanManageQualificationTemplates =
        new("lead:qualification-template:manage", "Create and Manage Qualification Templates", ApplicationModules.Leads, "qualification-template:manage");

    public static readonly PermissionItem CanRecordConsent =
        new("lead:consent:record", "Record Prospect Consent", ApplicationModules.Leads, "consent:record");

    public static readonly PermissionItem CanWithdrawConsent =
        new("lead:consent:withdraw", "Withdraw Prospect Consent", ApplicationModules.Leads, "consent:withdraw");

    public static readonly PermissionItem CanReadCrmTasks =
        new("lead:task:read", "View CRM Tasks", ApplicationModules.Leads, "task:read");

    public static readonly PermissionItem CanManageCrmTasks =
        new("lead:task:manage", "Create, Complete and Cancel CRM Tasks", ApplicationModules.Leads, "task:manage");

    public static readonly PermissionItem CanManageTaskGenerationRules =
        new("lead:task-rule:manage", "Configure Task Generation Rules", ApplicationModules.Leads, "task-rule:manage");

    // ── Lead configuration (US-M13-190..197) ────────────────────────────

    public static readonly PermissionItem CanManageLeadSources =
        new("lead:source:manage", "Create and Update Lead Sources", ApplicationModules.Leads, "source:manage");

    public static readonly PermissionItem CanReadLeadSources =
        new("lead:source:read", "Read Lead Sources", ApplicationModules.Leads, "source:read");

    public static readonly PermissionItem CanManageLeadSourceCredentials =
        new("lead:source:credentials", "Manage Lead Source Secrets", ApplicationModules.Leads, "source:credentials");

    public static readonly PermissionItem CanManageScoringConfigs =
        new("lead:scoring-config:manage", "Create and Activate Scoring Configurations", ApplicationModules.Leads, "scoring-config:manage");

    public static readonly PermissionItem CanReadScoringConfigs =
        new("lead:scoring-config:read", "Read Scoring Configurations", ApplicationModules.Leads, "scoring-config:read");

    public static readonly PermissionItem CanManageTaskTypes =
        new("lead:task-type:manage", "Create and Update Task Types", ApplicationModules.Leads, "task-type:manage");

    public static readonly PermissionItem CanReadTaskTypes =
        new("lead:task-type:read", "Read Task Types", ApplicationModules.Leads, "task-type:read");

    public static readonly PermissionItem CanManagePipelineStages =
        new("lead:pipeline-stage:manage", "Create and Update Pipeline Stage Configurations", ApplicationModules.Leads, "pipeline-stage:manage");

    public static readonly PermissionItem CanReadPipelineStages =
        new("lead:pipeline-stage:read", "Read Pipeline Stage Configurations", ApplicationModules.Leads, "pipeline-stage:read");

    public static readonly PermissionItem CanManageSlaConfigs =
        new("lead:sla-config:manage", "Create and Update SLA Configurations", ApplicationModules.Leads, "sla-config:manage");

    public static readonly PermissionItem CanReadSlaConfigs =
        new("lead:sla-config:read", "Read SLA Configurations", ApplicationModules.Leads, "sla-config:read");

    // ── Nurturing sequences (US-M13-150/151) ──────────────────────────────

    public static readonly PermissionItem CanManageNurturingSequences =
        new("lead:nurturing-sequence:manage", "Create and Update Nurturing Sequences", ApplicationModules.Leads, "nurturing-sequence:manage");

    public static readonly PermissionItem CanReadNurturingSequences =
        new("lead:nurturing-sequence:read", "Read Nurturing Sequences", ApplicationModules.Leads, "nurturing-sequence:read");

    // ── Opportunities (US-M13-130/131) ────────────────────────────────────

    public static readonly PermissionItem CanManageOpportunities =
        new("lead:opportunity:manage", "Create and Update Opportunities", ApplicationModules.Leads, "opportunity:manage");

    public static readonly PermissionItem CanReadOpportunities =
        new("lead:opportunity:read", "Read Opportunities", ApplicationModules.Leads, "opportunity:read");

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

    // ── Customers module (M01) ─────────────────────────────────────────────

    public static readonly PermissionItem CanReadCustomer =
        new("customers:read", "Read Customer Record", ApplicationModules.Customers, "read");

    public static readonly PermissionItem CanCreateCustomer =
        new("customers:create", "Create Customer Record", ApplicationModules.Customers, "create");

    public static readonly PermissionItem CanUpdateCustomer =
        new("customers:update", "Update Non-Sensitive Customer Data", ApplicationModules.Customers, "update");

    public static readonly PermissionItem CanUpdateCustomerSensitive =
        new("customers:update_sensitive", "Update Sensitive Customer Data (name, ID document, address)", ApplicationModules.Customers, "update_sensitive");

    public static readonly PermissionItem CanRevealCustomerSensitive =
        new("customers:reveal_sensitive", "Reveal a Single Encrypted Customer Field in Clear Text", ApplicationModules.Customers, "reveal_sensitive");

    public static readonly PermissionItem CanArchiveCustomer =
        new("customers:archive", "Archive a Customer Record", ApplicationModules.Customers, "archive");

    public static readonly PermissionItem CanMergeCustomers =
        new("customers:merge", "Request or Approve a Customer Merge", ApplicationModules.Customers, "merge");

    public static readonly PermissionItem CanManageCustomerGroups =
        new("customers:groups_manage", "Create and Manage Solidarity Groups, Tontines and VSLAs", ApplicationModules.Customers, "groups_manage");

    public static readonly PermissionItem CanExportCustomers =
        new("customers:export", "Export a Customer Search Result", ApplicationModules.Customers, "export");

    // ── KYC module (M02) ───────────────────────────────────────────────────
    // Read and the two write verbs are separate because a KYC file is evidence: an agent
    // collects and corrects, a manager decides, and only compliance lifts a duplicate flag.
    // Revealing a document number is its own permission for the same reason it is in M01 —
    // the value is encrypted at rest and the reveal is audited.

    public static readonly PermissionItem CanReadKycFile =
        new("kyc:read", "Read KYC File", ApplicationModules.Kyc, "read");

    public static readonly PermissionItem CanManageKycFile =
        new("kyc:manage", "Collect and correct KYC data", ApplicationModules.Kyc, "manage");

    public static readonly PermissionItem CanRunKycVerification =
        new("kyc:verify", "Run the biometric verification of a KYC file", ApplicationModules.Kyc, "verify");

    public static readonly PermissionItem CanApproveKycFile =
        new("kyc:approve", "Decide on a KYC file in the approval circuit", ApplicationModules.Kyc, "approve");

    /// <summary>Lifting a suspected-duplicate flag is a compliance act, never an agency one.</summary>
    public static readonly PermissionItem CanClearKycDuplicateFlag =
        new("kyc:duplicate:clear", "Clear a suspected duplicate document flag", ApplicationModules.Kyc, "duplicate:clear");

    public static readonly PermissionItem CanRevealKycDocumentNumber =
        new("kyc:document:reveal", "Reveal an identity document number", ApplicationModules.Kyc, "document:reveal");

    /// <summary>
    /// Accept or refuse an uploaded document, and validate a file's evidence by hand when the
    /// biometric service cannot.
    ///
    /// <para>
    /// Separate from <c>kyc:approve</c>, which signs a rung of the approval circuit under the
    /// four-eyes rule. This one is a single actor's judgement on an image: one decision, one author,
    /// audited. The circuit still gates the FILE afterwards, so the two are not substitutes — and
    /// whoever validates a file by hand is barred from then signing its ladder.
    /// </para>
    ///
    /// <para>
    /// Also separate from <c>kyc:document:reveal</c>, which gates SEEING the bytes. A validator
    /// needs both in practice; they are distinct because revealing is audited per access and
    /// deciding is audited per decision.
    /// </para>
    /// </summary>
    public static readonly PermissionItem CanValidateKycDocument =
        new("kyc:document:validate", "Accept or refuse a KYC document", ApplicationModules.Kyc, "document:validate");

    public static readonly PermissionItem CanManageKycSettings =
        new("kyc:settings:manage", "Change the tenant KYC parameters", ApplicationModules.Kyc, "settings:manage");

    // ── Integration module (INT-11, ASS-11) ───────────────────────────────
    //
    // These codes are PascalCase-dotted, which is the one place in this file that departs from
    // the `resource:action` convention of every other module. It is deliberate: the
    // specification tabulates them in this form together with their default roles, and the
    // front-end is generated against them. Nothing in AddSankoreAuthorization cares — a policy
    // name is an opaque string — and keeping them centralised here means a later alignment with
    // the repo convention is a single edit.

    public static readonly PermissionItem CanViewIntegrationConnection =
        new("Integration.Connection.View", "View an integration connection and its health",
            ApplicationModules.Integration, "Connection.View");

    public static readonly PermissionItem CanManageIntegrationConnection =
        new("Integration.Connection.Manage", "Create and update connections, secrets and relay agents",
            ApplicationModules.Integration, "Connection.Manage");

    public static readonly PermissionItem CanManageIntegrationMapping =
        new("Integration.Mapping.Manage", "Manage the code mapping tables",
            ApplicationModules.Integration, "Mapping.Manage");

    public static readonly PermissionItem CanViewIntegrationCommand =
        new("Integration.Command.View", "Read integration commands and the rejection queue",
            ApplicationModules.Integration, "Command.View");

    public static readonly PermissionItem CanReplayIntegrationCommand =
        new("Integration.Command.Replay", "Replay or cancel a rejected integration command",
            ApplicationModules.Integration, "Command.Replay");

    public static readonly PermissionItem CanViewIntegrationReconciliation =
        new("Integration.Reconciliation.View", "Read the reconciliation reports",
            ApplicationModules.Integration, "Reconciliation.View");

    public static readonly PermissionItem CanResolveIntegrationReconciliation =
        new("Integration.Reconciliation.Resolve", "Mark a reconciliation gap as resolved",
            ApplicationModules.Integration, "Reconciliation.Resolve");

    /// <summary>
    /// Triggers a DIRECT call to the core banking system. Separate from the read permissions
    /// because it is the one operation an agent can perform that puts load on the IMF's CBS.
    /// </summary>
    public static readonly PermissionItem CanViewLiveBalance =
        new("CoreBanking.Balance.ViewLive", "Trigger a direct balance call on the core banking system",
            ApplicationModules.Integration, "Balance.ViewLive");

    // ── Insurance family (ASS-11) ─────────────────────────────────────────

    public static readonly PermissionItem CanManageInsuranceProduct =
        new("Ins.Product.Manage", "Manage the tenant's insurance product catalogue",
            ApplicationModules.Integration, "Product.Manage");

    /// <summary>
    /// Read the catalogue and ask for a quote. <b>An eighth insurance permission, where ASS-11's
    /// table names seven</b> — a deliberate departure, recorded here rather than left to be
    /// discovered by whoever compares this file to the specification.
    ///
    /// <para>
    /// ASS-03's endpoints all sat behind <see cref="CanManageInsuranceProduct"/>, which means an
    /// agent holding only <c>Ins.Policy.Subscribe</c> could subscribe a policy and could not see
    /// which products exist or what one costs. That is not a separation of duties, it is a screen
    /// that cannot be drawn: ASS-11's own stated purpose is to « séparer distribution, gestion et
    /// administration », and reading the catalogue is part of DISTRIBUTING, not of managing.
    /// </para>
    ///
    /// <para>
    /// The alternative was to have the three read routes accept either code. Rejected: policies in
    /// this platform are generated one-per-permission by <c>AddSankoreAuthorization</c> and
    /// multiple <c>RequireAuthorization</c> calls are ANDed, so an OR would mean inventing a
    /// composite-policy mechanism for one screen — more machinery, and a second way to express an
    /// authorisation rule, which is how a catalogue stops being readable at a glance.
    /// </para>
    /// </summary>
    public static readonly PermissionItem CanViewInsuranceProduct =
        new("Ins.Product.View", "Read the insurance product catalogue and request a quote",
            ApplicationModules.Integration, "Product.View");

    public static readonly PermissionItem CanSubscribeInsurancePolicy =
        new("Ins.Policy.Subscribe", "Subscribe an insurance policy for a customer",
            ApplicationModules.Integration, "Policy.Subscribe");

    public static readonly PermissionItem CanViewInsurancePolicy =
        new("Ins.Policy.View", "Read a customer's insurance policies",
            ApplicationModules.Integration, "Policy.View");

    public static readonly PermissionItem CanDeclareInsuranceClaim =
        new("Ins.Claim.Declare", "Declare an insurance claim",
            ApplicationModules.Integration, "Claim.Declare");

    public static readonly PermissionItem CanViewInsuranceClaim =
        new("Ins.Claim.View", "Read insurance claims",
            ApplicationModules.Integration, "Claim.View");

    public static readonly PermissionItem CanViewInsuranceStatement =
        new("Ins.Statement.View", "Read the monthly insurer statements and commissions",
            ApplicationModules.Integration, "Statement.View");

    public static readonly PermissionItem CanResolveInsuranceReconciliation =
        new("Ins.Reconciliation.Resolve", "Mark an insurance reconciliation gap as resolved",
            ApplicationModules.Integration, "Reconciliation.Resolve");

    public static readonly PermissionItem[] All =
    [
        CanReadKycFile,
        CanManageKycFile,
        CanRunKycVerification,
        CanApproveKycFile,
        CanClearKycDuplicateFlag,
        CanRevealKycDocumentNumber,
        CanValidateKycDocument,
        CanManageKycSettings,
        CanCreateLoan,
        CanCreateAgency,
        CanReadAgency,
        CanUpdateAgency,
        CanDeleteAgency,
        CanActivateAgency,
        CanMoveAgency,
        CanAssignAgencyManager,
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
        CanMigrateObjectStorage,
        CanAssignRole,
        CanAssignUserAgency,
        CanAssignUserManager,
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
        CanManageQualificationTemplates,
        CanRecordConsent,
        CanWithdrawConsent,
        CanReadCrmTasks,
        CanManageCrmTasks,
        CanManageTaskGenerationRules,
        CanManageLeadSources,
        CanReadLeadSources,
        CanManageScoringConfigs,
        CanReadScoringConfigs,
        CanManageTaskTypes,
        CanReadTaskTypes,
        CanManagePipelineStages,
        CanReadPipelineStages,
        CanManageSlaConfigs,
        CanReadSlaConfigs,
        CanManageOpportunities,
        CanReadOpportunities,
        CanManageNurturingSequences,
        CanReadNurturingSequences,
        CanManageLeadSourceCredentials,
        CanReadCustomer,
        CanCreateCustomer,
        CanUpdateCustomer,
        CanUpdateCustomerSensitive,
        CanRevealCustomerSensitive,
        CanArchiveCustomer,
        CanMergeCustomers,
        CanManageCustomerGroups,
        CanExportCustomers,

        // Integration module (INT-11)
        CanViewIntegrationConnection,
        CanManageIntegrationConnection,
        CanManageIntegrationMapping,
        CanViewIntegrationCommand,
        CanReplayIntegrationCommand,
        CanViewIntegrationReconciliation,
        CanResolveIntegrationReconciliation,
        CanViewLiveBalance,

        // Insurance family (ASS-11), plus Ins.Product.View — see its own remarks for why the
        // specification's table of seven became eight.
        CanManageInsuranceProduct,
        CanViewInsuranceProduct,
        CanSubscribeInsurancePolicy,
        CanViewInsurancePolicy,
        CanDeclareInsuranceClaim,
        CanViewInsuranceClaim,
        CanViewInsuranceStatement,
        CanResolveInsuranceReconciliation,
    ];
}
