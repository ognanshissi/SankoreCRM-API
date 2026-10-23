using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Seeds the fixed set of system roles into the identity database on startup.
/// Idempotent — safe to run on every boot. System and Administrator roles
/// always receive ALL permissions, including any newly added ones.
/// </summary>
internal static class RoleSeeder
{
    internal static async Task SeedAsync(IServiceProvider sp)
    {
        var roleManager = sp.GetRequiredService<RoleManager<AppRole>>();
        var logger = sp.GetRequiredService<ILogger<AdministrationDbContext>>();
        var db = sp.GetRequiredService<AdministrationDbContext>();

        // 1. Seed permissions from Permissions.All
        await PermissionSeeder.SeedAsync(sp);

        // 2. Ensure all roles exist
        foreach (RoleItem role in Roles.All)
        {
            if (await roleManager.RoleExistsAsync(role.Code))
                continue;

            var result = await roleManager.CreateAsync(AppRole.Create(role.Code, role.Name, isSystem: true));
            if (!result.Succeeded)
                logger.LogWarning("Failed to seed role {Role}: {Errors}", role.Code,
                    string.Join("; ", result.Errors.Select(e => e.Description)));
        }

        await db.SaveChangesAsync();

        // 3. Grant ALL permissions to System and Administrator (idempotent).
        //    Runs every boot so new permissions are automatically granted.
        var allPermissions = await db.Permissions.ToListAsync();
        var privilegedRoles = new[] { Roles.System.Code, Roles.Administrator.Code };

        foreach (var roleCode in privilegedRoles)
        {
            var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == roleCode);
            if (role is null) continue;

            var grantedIds = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionId)
                .ToListAsync();

            var grantedSet = new HashSet<Guid>(grantedIds);

            foreach (var permission in allPermissions)
            {
                if (grantedSet.Contains(permission.Id))
                    continue;

                db.RolePermissions.Add(RolePermission.Grant(role.Id, permission.Id));
                logger.LogInformation("RoleSeeder: granted {Permission} to {Role}", permission.Code, roleCode);
            }
        }

        await db.SaveChangesAsync();

        // 4. Default grants to NON-privileged roles, from the declarative table below.
        //    Additive and idempotent: it never revokes, so a tenant that tightened a role by hand
        //    keeps its decision, and a permission added to the table reaches existing tenants on
        //    the next boot.
        await SeedDefaultGrantsAsync(db, logger);
    }

    // ── Default grants, role by role ───────────────────────────────────────
    //
    // Composed from the building blocks below rather than typed as (role, code) strings: a
    // PermissionItem that does not exist does not compile, where a mistyped string was only ever a
    // log line at boot. The blocks are declared BEFORE DefaultGrants because static readonly fields
    // initialise in textual order.
    //
    // What NO default role receives — System and Administrator hold it through the everything-grant,
    // anybody else is granted by hand through role:manage-permissions:
    //   - users, roles, agencies and territories (writes), impersonation, object-storage migration;
    //   - every tenant configuration screen: forms, notification and SMS settings, email quota,
    //     company info, integration connections and mappings, telephony settings, WhatsApp number
    //     and bot, lead sources and their secrets, scoring, pipeline stages, SLA, task types, lead
    //     settings, workflow templates and triggers;
    //   - tenant:modules:* — committing the institution to a cost is the administrator's act
    //     (TenantModulePermissionsTests pins it);
    //   - lead:health:read — special-category data the collector does not need to re-read;
    //   - lead:report:view — every team's figures, not one's own;
    //   - the insurance catalogue, the bordereau closure and both reconciliation resolutions.

    /// <summary>What every business role needs to work at all: its own agenda, its tasks, the reference lists.</summary>
    private static readonly PermissionItem[] EveryBusinessRole =
    [
        Permissions.CanUseConnectedAccounts,
        Permissions.CanReadCrmTasks,
        Permissions.CanManageCrmTasks,
        Permissions.CanReadCompanyInfo,
        Permissions.CanReadAgency,
        Permissions.CanReadUser,
        Permissions.CanReadProduct,
        Permissions.CanViewWorkflowInstances,
        Permissions.CanCompleteWorkflowTask,
        // Their own tickets to the platform team; support:read:all stays the administrator's.
        Permissions.CanReadSupportTickets,
        Permissions.CanCreateSupportTicket,
    ];

    /// <summary>Working one's own leads, from capture to conversion.</summary>
    private static readonly PermissionItem[] LeadWork =
    [
        Permissions.CanCaptureLead,
        Permissions.CanReadLead,
        Permissions.CanUpdateLead,
        Permissions.CanQualifyLead,
        Permissions.CanCloseLead,
        Permissions.CanMovePipelineStage,
        Permissions.CanLogLeadActivity,
        Permissions.CanManageLeadHousehold,
        Permissions.CanConvertLead,
        Permissions.CanNurtureLead,
        Permissions.CanManageLeadReminders,
        Permissions.CanTagLead,
        Permissions.CanRecordConsent,
        Permissions.CanWithdrawConsent,
        Permissions.CanReadOpportunities,
        Permissions.CanManageOpportunities,
        Permissions.CanReadLeadSources,
        Permissions.CanReadPipelineStages,
        Permissions.CanReadTaskTypes,
        Permissions.CanReadNurturingSequences,
    ];

    /// <summary>Watching and steering a team's leads, on top of working them.</summary>
    private static readonly PermissionItem[] LeadSupervision =
    [
        Permissions.CanAssignLead,
        Permissions.CanRecycleLead,
        Permissions.CanMergeLeads,
        Permissions.CanDismissDuplicate,
        Permissions.CanImportLeads,
        Permissions.CanExportLeads,
        Permissions.CanViewLeadAnalytics,
        Permissions.CanReadDispatchingRules,
        Permissions.CanReadScoringConfigs,
        Permissions.CanReadSlaConfigs,
    ];

    /// <summary>The customer record as a front-line role keeps it.</summary>
    private static readonly PermissionItem[] CustomerWork =
    [
        Permissions.CanReadCustomer,
        Permissions.CanCreateCustomer,
        Permissions.CanUpdateCustomer,
        Permissions.CanRevealCustomerSensitive,
        Permissions.CanManageCustomerHousehold,
        Permissions.CanManageCustomerHolding,
        Permissions.CanManageDesignations,
    ];

    /// <summary>
    /// Collecting a KYC file and signing its first rung. kyc:approve is part of it because
    /// KycApproverLevels gives Agent and CommercialAgent the agent rung of the ladder.
    /// </summary>
    private static readonly PermissionItem[] KycCollection =
    [
        Permissions.CanReadKycFile,
        Permissions.CanManageKycFile,
        Permissions.CanRunKycVerification,
        Permissions.CanApproveKycFile,
        Permissions.CanInviteKycRemote,
    ];

    /// <summary>
    /// The whole perimeter rather than one's own records — every business role but the two agents
    /// (product owner, 2026-10-23: an Agent or a CommercialAgent consults only the leads, clients
    /// and KYC files assigned to them). A tenant restores the wide view by granting these.
    /// </summary>
    private static readonly PermissionItem[] ReadEverythingInPerimeter =
    [
        Permissions.CanReadAllLeads,
        Permissions.CanReadAllCustomers,
        Permissions.CanReadAllKycFiles,
    ];

    private static readonly PermissionItem[] TelephonyAgent =
    [
        Permissions.CanMakeTelephonyCall,
        Permissions.CanViewTelephonyCall,
        Permissions.CanLinkTelephonyCall,
    ];

    /// <summary>An agent places calls; a supervisor also listens to them and reads the indicators.</summary>
    private static readonly PermissionItem[] TelephonySupervisor =
    [
        .. TelephonyAgent,
        Permissions.CanListenTelephonyRecording,
        Permissions.CanViewTelephonyStats,
    ];

    /// <summary>Distributing insurance: the catalogue, a quote, a policy, a claim.</summary>
    private static readonly PermissionItem[] InsuranceDistribution =
    [
        Permissions.CanViewInsuranceProduct,
        Permissions.CanSubscribeInsurancePolicy,
        Permissions.CanViewInsurancePolicy,
        Permissions.CanDeclareInsuranceClaim,
        Permissions.CanViewInsuranceClaim,
    ];

    private static readonly PermissionItem[] WorkflowSupervisor =
    [
        Permissions.CanReadWorkflow,
        Permissions.CanStartWorkflow,
        Permissions.CanApproveWorkflow,
        Permissions.CanCancelWorkflow,
        Permissions.CanAssignWorkflowStep,
        Permissions.CanViewWorkflowAnalytics,
    ];

    /// <summary>Counter agent: leads, customers, KYC collection, the live balance at the counter.</summary>
    private static readonly PermissionItem[] AgentPermissions =
    [
        .. EveryBusinessRole,
        .. LeadWork,
        .. CustomerWork,
        Permissions.CanUpdateCustomerSensitive,
        Permissions.CanManageCustomerGroups,
        .. KycCollection,
        Permissions.CanRevealKycDocumentNumber,
        Permissions.CanViewLiveBalance,
        .. TelephonyAgent,
        Permissions.CanSendWhatsApp,
        Permissions.CanReplyWhatsApp,
        .. InsuranceDistribution,
        Permissions.CanStartWorkflow,
    ];

    /// <summary>
    /// Field agent: the counter agent's lead, customer and KYC work, without correcting a customer's
    /// identity or reading a document number away from the branch.
    /// </summary>
    private static readonly PermissionItem[] CommercialAgentPermissions =
    [
        .. EveryBusinessRole,
        .. LeadWork,
        .. CustomerWork,
        Permissions.CanManageCustomerGroups,
        .. KycCollection,
        Permissions.CanViewLiveBalance,
        .. TelephonyAgent,
        Permissions.CanSendWhatsApp,
        Permissions.CanReplyWhatsApp,
        .. InsuranceDistribution,
        Permissions.CanStartWorkflow,
    ];

    /// <summary>
    /// Sales supervisor: the team's pipeline and its commercial rules — dispatching, nurturing, task
    /// generation, qualification templates. Scoring, stages and SLA stay the administrator's.
    /// </summary>
    private static readonly PermissionItem[] SalesManagerPermissions =
    [
        .. EveryBusinessRole,
        .. ReadEverythingInPerimeter,
        Permissions.CanReadTerritory,
        .. LeadWork,
        .. LeadSupervision,
        Permissions.CanManageDispatchingRules,
        Permissions.CanManageNurturingSequences,
        Permissions.CanManageTaskGenerationRules,
        Permissions.CanManageQualificationTemplates,
        Permissions.CanReadCustomer,
        Permissions.CanCreateCustomer,
        Permissions.CanUpdateCustomer,
        Permissions.CanExportCustomers,
        Permissions.CanReadKycFile,
        Permissions.CanViewIntegrationCommand,
        Permissions.CanViewLiveBalance,
        .. TelephonySupervisor,
        Permissions.CanSendWhatsApp,
        Permissions.CanReplyWhatsApp,
        Permissions.CanForceWhatsAppSend,
        Permissions.CanSuperviseWhatsApp,
        .. InsuranceDistribution,
        Permissions.CanViewInsuranceStatement,
        .. WorkflowSupervisor,
    ];

    /// <summary>
    /// Agency head: everything done in the branch, plus the branch-manager rung of the KYC ladder,
    /// the manual document check, the customer merge and the PEP detail.
    /// </summary>
    private static readonly PermissionItem[] BranchManagerPermissions =
    [
        .. EveryBusinessRole,
        .. ReadEverythingInPerimeter,
        Permissions.CanReadTerritory,
        .. LeadWork,
        .. LeadSupervision,
        Permissions.CanReadLeadPep,
        .. CustomerWork,
        Permissions.CanUpdateCustomerSensitive,
        Permissions.CanManageCustomerGroups,
        Permissions.CanArchiveCustomer,
        Permissions.CanMergeCustomers,
        Permissions.CanExportCustomers,
        Permissions.CanReadCustomerPep,
        .. KycCollection,
        Permissions.CanRevealKycDocumentNumber,
        Permissions.CanValidateKycDocument,
        Permissions.CanViewIntegrationCommand,
        Permissions.CanViewLiveBalance,
        .. TelephonySupervisor,
        Permissions.CanSendWhatsApp,
        Permissions.CanReplyWhatsApp,
        Permissions.CanForceWhatsAppSend,
        Permissions.CanSuperviseWhatsApp,
        .. InsuranceDistribution,
        Permissions.CanViewInsuranceStatement,
        .. WorkflowSupervisor,
    ];

    /// <summary>
    /// Cashier: reads the customer and their balance, and refers a prospect met at the till. No
    /// customer or KYC write — a till is not where a file is built.
    /// </summary>
    private static readonly PermissionItem[] CashierPermissions =
    [
        .. EveryBusinessRole,
        .. ReadEverythingInPerimeter,
        Permissions.CanCaptureLead,
        Permissions.CanReadLead,
        Permissions.CanReadCustomer,
        Permissions.CanReadKycFile,
        Permissions.CanViewLiveBalance,
        Permissions.CanViewInsurancePolicy,
    ];

    /// <summary>
    /// Compliance and internal control: reads everything it controls, signs the compliance rung,
    /// owns the duplicate flags, the KYC and FATCA/PEP parameters, the sample of automatic
    /// approvals, the professional-card register, the call-recording exports and the complaints
    /// register. It sells nothing and edits no customer.
    /// </summary>
    private static readonly PermissionItem[] RegulationManagerPermissions =
    [
        .. EveryBusinessRole,
        .. ReadEverythingInPerimeter,
        Permissions.CanReadTerritory,
        Permissions.CanReadAudit,
        Permissions.CanManageDistributionCredentials,
        Permissions.CanReadCompliance,
        Permissions.CanManageCompliance,
        Permissions.CanReadLead,
        Permissions.CanReadLeadPep,
        Permissions.CanReadCustomer,
        Permissions.CanRevealCustomerSensitive,
        Permissions.CanReadCustomerPep,
        Permissions.CanExportCustomers,
        Permissions.CanReadKycFile,
        Permissions.CanApproveKycFile,
        Permissions.CanClearKycDuplicateFlag,
        Permissions.CanRevealKycDocumentNumber,
        Permissions.CanValidateKycDocument,
        Permissions.CanManageKycSettings,
        Permissions.CanControlKycAutoApproval,
        Permissions.CanViewIntegrationCommand,
        Permissions.CanViewIntegrationReconciliation,
        Permissions.CanViewTelephonyCall,
        Permissions.CanListenTelephonyRecording,
        Permissions.CanExportTelephonyRecording,
        Permissions.CanViewTelephonyStats,
        Permissions.CanViewInsuranceProduct,
        Permissions.CanViewInsurancePolicy,
        Permissions.CanViewInsuranceClaim,
        Permissions.CanViewInsuranceStatement,
        Permissions.CanViewInsuranceComplaint,
        Permissions.CanHandleInsuranceComplaint,
        Permissions.CanReadWorkflow,
        Permissions.CanApproveWorkflow,
        Permissions.CanViewWorkflowAnalytics,
    ];

    /// <summary>
    /// Permissions a role holds out of the box, beyond the everything-grant that System and
    /// Administrator receive above.
    ///
    /// <para>
    /// Additive: the seeder never revokes, so a tenant that tightened a role by hand keeps its
    /// decision, and a grant added here reaches existing tenants on the next boot. Removing one from
    /// this table therefore withdraws it from NEW tenants only.
    /// </para>
    ///
    /// <para>
    /// <b>Role mapping.</b> The specifications name a « superviseur » and a « contrôle interne »,
    /// neither of which is a role of this platform. They are read as the closest existing ones —
    /// <see cref="Roles.SalesManager"/> and <see cref="Roles.BranchManager"/> for a supervisor,
    /// <see cref="Roles.RegulationManager"/> for internal control and compliance (the same reading
    /// KycApproverLevels makes for the compliance rung).
    /// </para>
    /// </summary>
    internal static readonly (string RoleCode, string PermissionCode)[] DefaultGrants =
    [
        .. Grant(Roles.Agent, AgentPermissions),
        .. Grant(Roles.CommercialAgent, CommercialAgentPermissions),
        .. Grant(Roles.SalesManager, SalesManagerPermissions),
        .. Grant(Roles.BranchManager, BranchManagerPermissions),
        .. Grant(Roles.Cashier, CashierPermissions),
        .. Grant(Roles.RegulationManager, RegulationManagerPermissions),
    ];

    private static IEnumerable<(string RoleCode, string PermissionCode)> Grant(
        RoleItem role, IEnumerable<PermissionItem> permissions)
        => permissions.Select(p => p.Code).Distinct(StringComparer.Ordinal).Select(code => (role.Code, code));

    private static async Task SeedDefaultGrantsAsync(AdministrationDbContext db, ILogger logger)
    {
        var wanted = DefaultGrants
            .GroupBy(g => g.RoleCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PermissionCode).ToArray(), StringComparer.Ordinal);

        var permissionsByCode = await db.Permissions
            .ToDictionaryAsync(p => p.Code, p => p.Id, StringComparer.Ordinal);

        var added = 0;

        foreach (var (roleCode, permissionCodes) in wanted)
        {
            var role = await db.Roles.FirstOrDefaultAsync(x => x.Name == roleCode);
            if (role is null)
            {
                logger.LogWarning(
                    "RoleSeeder: role {Role} is absent, so its default grants were skipped", roleCode);
                continue;
            }

            var grantedIds = await db.RolePermissions
                .Where(rp => rp.RoleId == role.Id)
                .Select(rp => rp.PermissionId)
                .ToListAsync();

            var grantedSet = new HashSet<Guid>(grantedIds);

            foreach (var code in permissionCodes)
            {
                // A code in the table that is not in Permissions.All is a typo, and a silent one:
                // the grant would simply never happen. Logged loudly rather than ignored.
                if (!permissionsByCode.TryGetValue(code, out var permissionId))
                {
                    logger.LogError(
                        "RoleSeeder: default grant references unknown permission {Permission}; "
                        + "add it to Permissions.All or fix the code", code);
                    continue;
                }

                if (grantedSet.Contains(permissionId)) continue;

                db.RolePermissions.Add(RolePermission.Grant(role.Id, permissionId));
                grantedSet.Add(permissionId);
                added++;

                logger.LogInformation(
                    "RoleSeeder: granted default {Permission} to {Role}", code, roleCode);
            }
        }

        if (added == 0) return;

        await db.SaveChangesAsync();
    }
}
