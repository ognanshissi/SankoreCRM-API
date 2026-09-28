namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// US-M01-BE-01 acceptance criteria on the permission catalogue.
/// <para>
/// These are not cosmetic assertions. <c>AddSankoreAuthorization()</c> generates exactly one
/// ASP.NET policy per entry of <see cref="Permissions.All"/>, and every M01 endpoint calls
/// <c>.RequireAuthorization("customers:…")</c>. A permission declared but left out of
/// <c>All</c> therefore has no policy, and the endpoint guarding itself with it fails at
/// runtime with an unhelpful error — not at startup. A typo in a code has the same effect,
/// and <c>RoleSeeder</c> would silently grant nothing.
/// </para>
/// </summary>
public sealed class CustomerPermissionsTests
{
    /// <summary>The nine codes named by the US, verbatim. This array IS the specification.</summary>
    private static readonly string[] ExpectedCodes =
    [
        "customers:read",
        "customers:create",
        "customers:update",
        "customers:update_sensitive",
        "customers:reveal_sensitive",
        "customers:archive",
        "customers:merge",
        "customers:groups_manage",
        "customers:export"
    ];

    [Fact]
    public void The_nine_customer_permissions_are_registered_in_Permissions_All()
    {
        var registered = Permissions.All.Select(p => p.Code).ToList();

        foreach (var code in ExpectedCodes)
            registered.Should().Contain(code, $"endpoints call RequireAuthorization(\"{code}\")");
    }

    [Fact]
    public void Every_customer_permission_is_attached_to_the_Customers_module()
    {
        var customerPermissions = Permissions.All
            .Where(p => ExpectedCodes.Contains(p.Code))
            .ToList();

        customerPermissions.Should().HaveCount(ExpectedCodes.Length);
        customerPermissions.Should().OnlyContain(p => p.Module == ApplicationModules.Customers);
    }

    [Fact]
    public void No_customer_permission_code_is_declared_twice()
    {
        // A duplicate would make AddSankoreAuthorization register the same policy name twice and
        // PermissionSeeder insert a duplicate row.
        var duplicates = Permissions.All
            .Where(p => p.Code.StartsWith("customers:", StringComparison.Ordinal))
            .GroupBy(p => p.Code, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.Should().BeEmpty();
    }

    [Fact]
    public void The_Customers_module_declares_no_permission_beyond_the_nine_of_the_specification()
    {
        // Guards the other direction: an extra "customers:*" permission slipped in by another
        // slice would be granted by RoleSeeder without ever appearing in the US.
        var codes = Permissions.All
            .Where(p => p.Code.StartsWith("customers:", StringComparison.Ordinal))
            .Select(p => p.Code);

        codes.Should().BeEquivalentTo(ExpectedCodes);
    }

    [Fact]
    public void Every_customer_permission_carries_a_description_and_an_action()
    {
        var customerPermissions = Permissions.All
            .Where(p => p.Code.StartsWith("customers:", StringComparison.Ordinal))
            .ToList();

        customerPermissions.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Description));
        customerPermissions.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Action));

        // The action is the part after the colon — the admin UI groups by it.
        customerPermissions.Should().OnlyContain(p => p.Code == $"customers:{p.Action}");
    }

    [Fact]
    public void The_permission_constants_used_by_the_Compliance_endpoints_resolve_to_the_expected_codes()
    {
        // These four constants are the ones the zone's endpoints pass to RequireAuthorization.
        Permissions.CanReadCustomer.Code.Should().Be("customers:read");
        Permissions.CanUpdateCustomerSensitive.Code.Should().Be("customers:update_sensitive");
        Permissions.CanArchiveCustomer.Code.Should().Be("customers:archive");
        Permissions.CanExportCustomers.Code.Should().Be("customers:export");
    }
}
