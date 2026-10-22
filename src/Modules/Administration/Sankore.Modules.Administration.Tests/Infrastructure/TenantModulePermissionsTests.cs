namespace Sankore.Modules.Administration.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The permissions of the paid tenant modules (« Banking - Microfinance », « Assurance »).
///
/// <para>
/// A permission left out of <see cref="Permissions.All"/> has no ASP.NET policy, so the endpoint
/// guarding itself with it fails at run time, and <c>RoleSeeder</c> grants it to nobody — not
/// even Administrator. And a code slipped into <see cref="RoleSeeder.DefaultGrants"/> would hand
/// an agent the power to commit the institution to a cost, or to switch a module off: the product
/// owner made both the administrator's acts.
/// </para>
/// </summary>
public sealed class TenantModulePermissionsTests
{
    private static readonly string[] TenantModuleCodes =
    [
        "tenant:modules:read",
        "tenant:modules:request",
        "tenant:modules:deactivate",
    ];

    [Fact]
    public void Every_tenant_module_permission_is_registered_in_Permissions_All()
    {
        Permissions.All.Select(p => p.Code).Should().Contain(TenantModuleCodes);
    }

    [Fact]
    public void They_belong_to_the_administration_module_so_the_role_editor_lists_them_there()
    {
        Permissions.All
            .Where(p => TenantModuleCodes.Contains(p.Code))
            .Should().HaveCount(TenantModuleCodes.Length)
            .And.OnlyContain(p => p.Module == ApplicationModules.Administration);
    }

    [Fact]
    public void No_role_receives_them_by_default_beyond_the_administrator_everything_grant()
    {
        RoleSeeder.DefaultGrants.Should().NotBeEmpty("an empty table would make the next assertion vacuous");

        RoleSeeder.DefaultGrants
            .Select(g => g.PermissionCode)
            .Should().NotContain(TenantModuleCodes);
    }
}
