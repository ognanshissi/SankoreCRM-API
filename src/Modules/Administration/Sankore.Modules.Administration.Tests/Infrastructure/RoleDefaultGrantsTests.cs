namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The out-of-the-box permissions of the six non-privileged roles. The seeder never revokes, so a
/// grant that slips in here reaches every existing tenant on the next boot and stays there.
/// </summary>
public sealed class RoleDefaultGrantsTests
{
    private static readonly RoleItem[] NonPrivilegedRoles =
        Roles.All.Where(r => r != Roles.System && r != Roles.Administrator).ToArray();

    /// <summary>Administrator-only by decision; anybody else is granted them by hand.</summary>
    private static readonly PermissionItem[] AdministratorOnly =
    [
        Permissions.CanCreateUser,
        Permissions.CanUpdateUser,
        Permissions.CanImpersonateUser,
        Permissions.CanAssignRole,
        Permissions.CanManageRolePermissions,
        Permissions.CanMigrateObjectStorage,
        Permissions.CanReadTenantModules,
        Permissions.CanRequestTenantModule,
        Permissions.CanDeactivateTenantModule,
        Permissions.CanManageEntityForms,
        Permissions.CanManageCustomerSegments,
        Permissions.CanManageNotificationSettings,
        Permissions.CanManageIntegrationConnection,
        Permissions.CanViewWebhooks,
        Permissions.CanManageWebhooks,
        Permissions.CanManageTelephonySettings,
        Permissions.CanConfigureWhatsApp,
        Permissions.CanConfigureWhatsAppBot,
        Permissions.CanManageLeadSourceCredentials,
        Permissions.CanManageInsuranceProduct,
        Permissions.CanReadLeadHealth,
    ];

    [Fact]
    public void Every_granted_code_is_a_registered_permission()
    {
        var registered = Permissions.All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

        RoleSeeder.DefaultGrants.Should().OnlyContain(g => registered.Contains(g.PermissionCode));
    }

    [Fact]
    public void Every_non_privileged_role_receives_a_default_set()
    {
        var roles = RoleSeeder.DefaultGrants.Select(g => g.RoleCode).ToHashSet(StringComparer.Ordinal);

        roles.Should().BeEquivalentTo(NonPrivilegedRoles.Select(r => r.Code));
    }

    [Fact]
    public void No_grant_is_listed_twice()
    {
        RoleSeeder.DefaultGrants.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Administrator_only_permissions_are_granted_to_no_default_role()
    {
        RoleSeeder.DefaultGrants
            .Select(g => g.PermissionCode)
            .Should().NotContain(AdministratorOnly.Select(p => p.Code));
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("CommercialAgent")]
    [InlineData("AccountManager")]
    [InlineData("CreditManager")]
    [InlineData("BranchManager")]
    [InlineData("RegulationManager")]
    public void Every_role_on_the_KYC_ladder_can_sign_its_rung(string roleCode)
    {
        Granted(roleCode).Should().Contain(Permissions.CanApproveKycFile.Code);
    }

    [Fact]
    public void Only_compliance_lifts_a_duplicate_flag_and_controls_automatic_approvals()
    {
        foreach (var role in NonPrivilegedRoles.Where(r => r != Roles.RegulationManager))
        {
            Granted(role.Code).Should().NotContain(
                [Permissions.CanClearKycDuplicateFlag.Code, Permissions.CanControlKycAutoApproval.Code],
                role.Code);
        }

        Granted(Roles.RegulationManager.Code).Should().Contain(
            [Permissions.CanClearKycDuplicateFlag.Code, Permissions.CanControlKycAutoApproval.Code]);
    }

    private static IEnumerable<string> Granted(string roleCode)
        => RoleSeeder.DefaultGrants.Where(g => g.RoleCode == roleCode).Select(g => g.PermissionCode);

    private static readonly PermissionItem[] ReadAll =
    [
        Permissions.CanReadAllLeads,
        Permissions.CanReadAllCustomers,
        Permissions.CanReadAllKycFiles,
    ];

    [Theory]
    [InlineData("SalesManager")]
    [InlineData("BranchManager")]
    public void The_two_managers_supervise_a_team(string role)
    {
        Granted(role).Should().Contain(Permissions.CanSuperviseTeam.Code);
    }

    [Fact]
    public void Only_the_sales_manager_covers_every_agency_by_default()
    {
        RoleSeeder.DefaultGrants.Where(g => g.PermissionCode == Permissions.CanCoverAllAgencies.Code)
            .Select(g => g.RoleCode).Should().Equal(Roles.SalesManager.Code);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("CommercialAgent")]
    [InlineData("AccountManager")]
    [InlineData("CreditManager")]
    [InlineData("CreditAnalyst")]
    public void The_front_line_roles_do_not_open_the_settings(string role)
    {
        Granted(role).Should().NotContain(Permissions.CanAccessSettings.Code);
    }

    [Fact]
    public void The_credit_analyst_reads_the_perimeter_and_edits_nothing()
    {
        Granted(Roles.CreditAnalyst.Code).Should().Contain(ReadAll.Select(p => p.Code));
        Granted(Roles.CreditAnalyst.Code).Should().Contain(Permissions.CanApproveWorkflow.Code);
        Granted(Roles.CreditAnalyst.Code).Should().NotContain(
            [Permissions.CanUpdateLead.Code, Permissions.CanUpdateCustomer.Code, Permissions.CanManageOpportunities.Code,
             Permissions.CanManageKycFile.Code, Permissions.CanSuperviseTeam.Code]);
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("CommercialAgent")]
    [InlineData("AccountManager")]
    [InlineData("CreditManager")]
    public void The_two_agent_roles_read_only_what_is_assigned_to_them(string role)
    {
        // Product owner, 2026-10-23. A grant here would reach every existing tenant on the next
        // boot and widen every agent's view back to the whole perimeter.
        var granted = RoleSeeder.DefaultGrants.Where(g => g.RoleCode == role).Select(g => g.PermissionCode);

        granted.Should().NotIntersectWith(ReadAll.Select(p => p.Code));
    }

    [Theory]
    [InlineData("SalesManager")]
    [InlineData("BranchManager")]
    [InlineData("Cashier")]
    [InlineData("RegulationManager")]
    public void The_other_business_roles_keep_the_whole_perimeter(string role)
    {
        var granted = RoleSeeder.DefaultGrants.Where(g => g.RoleCode == role).Select(g => g.PermissionCode);

        granted.Should().Contain(ReadAll.Select(p => p.Code));
    }

    [Theory]
    [InlineData("Agent", false)]
    [InlineData("CommercialAgent", false)]
    [InlineData("SalesManager", true)]
    [InlineData("BranchManager", true)]
    public void Only_managers_choose_a_clients_advisor(string role, bool granted)
    {
        // Product owner, 2026-11-02: an agent neither assigns a client nor takes one for themselves.
        RoleSeeder.DefaultGrants.Where(g => g.RoleCode == role).Select(g => g.PermissionCode)
            .Contains(Permissions.CanAssignCustomerAdvisor.Code).Should().Be(granted);
    }

    [Theory]
    [InlineData("SalesManager", true)]
    [InlineData("BranchManager", true)]
    [InlineData("RegulationManager", true)]
    [InlineData("Agent", false)]
    [InlineData("CommercialAgent", false)]
    [InlineData("Cashier", false)]
    public void Managers_and_compliance_open_the_reports(string role, bool granted)
    {
        // Product owner, 2026-11-02: the reports (leads, projects, clients, KYC) are for managers and
        // compliance; agents keep their own figures in « Ma journée ».
        RoleSeeder.DefaultGrants.Where(g => g.RoleCode == role).Select(g => g.PermissionCode)
            .Contains(Permissions.CanViewLeadReports.Code).Should().Be(granted);
    }
}
