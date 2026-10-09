namespace Sankore.Modules.Integration.Tests.Conventions;

using FluentAssertions;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-11 and ASS-11 on the permission catalogue.
///
/// <para>
/// These are not cosmetic assertions. <c>AddSankoreAuthorization()</c> generates exactly one
/// ASP.NET policy per entry of <see cref="Permissions.All"/>, and every endpoint of this module
/// calls <c>.RequireAuthorization(Permissions.X.Code)</c>. A permission declared but left out of
/// <c>All</c> therefore has no policy, and the endpoint guarding itself with it fails at
/// <b>run time</b> with an unhelpful error — not at start-up. <c>PermissionSeeder</c> would
/// silently grant nothing, and <c>RoleSeeder</c> would grant nothing to anybody.
/// </para>
/// </summary>
public sealed class IntegrationPermissionsTests
{
    /// <summary>The eight codes of INT-11's table, verbatim. This array IS the specification.</summary>
    private static readonly string[] IntegrationCodes =
    [
        "Integration.Connection.View",
        "Integration.Connection.Manage",
        "Integration.Mapping.Manage",
        "Integration.Command.View",
        "Integration.Command.Replay",
        "Integration.Reconciliation.View",
        "Integration.Reconciliation.Resolve",
        "CoreBanking.Balance.ViewLive",
    ];

    /// <summary>The seven codes of ASS-11's table, verbatim.</summary>
    private static readonly string[] InsuranceCodes =
    [
        "Ins.Product.Manage",
        "Ins.Policy.Subscribe",
        "Ins.Policy.View",
        "Ins.Claim.Declare",
        "Ins.Claim.View",
        "Ins.Statement.View",
        "Ins.Reconciliation.Resolve",
    ];

    [Fact]
    public void The_eight_integration_permissions_are_registered_in_Permissions_All()
    {
        var registered = Permissions.All.Select(p => p.Code).ToList();

        foreach (var code in IntegrationCodes)
            registered.Should().Contain(code, $"endpoints call RequireAuthorization(\"{code}\")");
    }

    [Fact]
    public void The_seven_insurance_permissions_are_registered_in_Permissions_All()
    {
        var registered = Permissions.All.Select(p => p.Code).ToList();

        foreach (var code in InsuranceCodes)
            registered.Should().Contain(code, $"endpoints call RequireAuthorization(\"{code}\")");
    }

    [Fact]
    public void The_module_declares_no_permission_beyond_the_two_specification_tables()
    {
        var declared = Permissions.All
            .Where(p => p.Module == ApplicationModules.Integration)
            .Select(p => p.Code)
            .ToList();

        // Guards the other direction: a permission invented in passing would widen the surface
        // an administrator has to reason about, and nothing else would report it.
        declared.Should().BeEquivalentTo([.. IntegrationCodes, .. InsuranceCodes]);
    }

    [Fact]
    public void Every_permission_of_the_module_carries_a_description_and_an_action()
    {
        var declared = Permissions.All
            .Where(p => p.Module == ApplicationModules.Integration)
            .ToList();

        declared.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Description));
        declared.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Action));
    }

    [Fact]
    public void No_code_is_declared_twice_across_the_whole_catalogue()
    {
        // A duplicate would make AddSankoreAuthorization register the same policy twice, and the
        // seeder's insert-if-absent would quietly keep only the first description.
        Permissions.All.Select(p => p.Code)
            .Should().OnlyHaveUniqueItems("one policy is generated per code");
    }

    /// <summary>
    /// The one place in the catalogue that departs from the repo's <c>resource:action</c>
    /// convention, pinned deliberately so the deviation is a decision and not a drift.
    ///
    /// <para>
    /// The specification tabulates these codes in PascalCase-dotted form together with their
    /// default roles, and the front-end client is generated against them. Nothing in
    /// <c>AddSankoreAuthorization</c> cares — a policy name is an opaque string. If the project
    /// later aligns them with <c>integration:connection:view</c>, this test is where the change
    /// is declared.
    /// </para>
    /// </summary>
    [Fact]
    public void The_module_codes_follow_the_specifications_dotted_form()
    {
        var declared = Permissions.All
            .Where(p => p.Module == ApplicationModules.Integration)
            .ToList();

        declared.Should().OnlyContain(
            p => p.Code.Contains('.', StringComparison.Ordinal)
                 && !p.Code.Contains(':', StringComparison.Ordinal),
            "INT-11 and ASS-11 name them this way; see docs/integration-module-plan.md §1");

        // And the Action is the part after the first dot, so an administration screen can group
        // by it exactly as it does for the colon-separated modules.
        declared.Should().OnlyContain(
            p => p.Code.EndsWith(p.Action, StringComparison.Ordinal));
    }
}
