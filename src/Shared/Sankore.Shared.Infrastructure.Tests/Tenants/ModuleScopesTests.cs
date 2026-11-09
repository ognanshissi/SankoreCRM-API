namespace Sankore.Shared.Infrastructure.Tests.Tenants;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Which paid module a permission or a built-in role belongs to (docs/isolation-socle.md, lot 2).
/// The socle keeps what telephony, mail and Standard products use.
/// </summary>
public sealed class ModuleScopesTests
{
    [Fact]
    public void The_credit_and_cash_roles_are_banking_ones()
    {
        foreach (var role in new[] { Roles.CreditAnalyst, Roles.CreditManager, Roles.Cashier, Roles.AccountManager })
            ModuleScopes.OfRole(role.Code).Should().Equal(TenantModules.BankingMicrofinance);

        foreach (var role in new[] { Roles.Administrator, Roles.Agent, Roles.CommercialAgent, Roles.BranchManager, Roles.SalesManager, Roles.RegulationManager })
            ModuleScopes.OfRole(role.Code).Should().BeEmpty();
        ModuleScopes.OfRole("RoleDeLInstitution").Should().BeEmpty();
    }

    [Fact]
    public void Every_insurance_permission_is_the_insurance_modules()
    {
        Permissions.All.Where(p => p.Code.StartsWith("Ins.", StringComparison.Ordinal))
            .Should().NotBeEmpty()
            .And.OnlyContain(p => ModuleScopes.OfPermission(p.Code).SequenceEqual(new[] { TenantModules.Insurance }));
        ModuleScopes.OfPermission(Permissions.CanManageDistributionCredentials.Code).Should().Equal(TenantModules.Insurance);
        ModuleScopes.OfPermission(Permissions.CanReadLeadHealth.Code).Should().Equal(TenantModules.Insurance);
    }

    [Fact]
    public void Loans_groups_and_live_balance_are_banking_and_the_integration_surfaces_either()
    {
        ModuleScopes.OfPermission(Permissions.CanCreateLoan.Code).Should().Equal(TenantModules.BankingMicrofinance);
        ModuleScopes.OfPermission(Permissions.CanManageCustomerGroups.Code).Should().Equal(TenantModules.BankingMicrofinance);
        ModuleScopes.OfPermission(Permissions.CanViewLiveBalance.Code).Should().Equal(TenantModules.BankingMicrofinance);
        ModuleScopes.OfPermission(Permissions.CanViewIntegrationCommand.Code)
            .Should().BeEquivalentTo([TenantModules.BankingMicrofinance, TenantModules.Insurance]);
    }

    [Fact]
    public void Connections_webhooks_and_holdings_stay_in_the_socle()
    {
        ModuleScopes.OfPermission(Permissions.CanViewIntegrationConnection.Code).Should().BeEmpty();
        ModuleScopes.OfPermission(Permissions.CanManageCustomerHolding.Code).Should().BeEmpty();
        ModuleScopes.OfPermission(Permissions.CanReadCrmTasks.Code).Should().BeEmpty();
    }

    [Fact]
    public void Something_is_in_reach_through_any_of_its_modules()
    {
        var insurance = new HashSet<string> { TenantModules.Insurance };
        ModuleScopes.InReach([], new HashSet<string>()).Should().BeTrue();
        ModuleScopes.InReach([TenantModules.BankingMicrofinance], insurance).Should().BeFalse();
        ModuleScopes.InReach([TenantModules.BankingMicrofinance, TenantModules.Insurance], insurance).Should().BeTrue();
    }
}
