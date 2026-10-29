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

    /// <summary>
    /// The one code of the WhatsApp specification's permission table that belongs to this module —
    /// configuring the number and its templates. The agent-side codes (send, force, supervise) are
    /// declared under M13, which owns the conversations.
    /// </summary>
    private static readonly string[] MessagingCodes =
    [
        "WhatsApp.Config",
    ];

    /// <summary>Connected agendas and mailbox: a user's OWN accounts, and the institution's e-mail templates.</summary>
    private static readonly string[] ConnectedAccountCodes =
    [
        "ConnectedAccounts.Use",
        "MailTemplates.Manage",
    ];

    /// <summary>Outbound webhooks: seeing them, and deciding which events leave and where.</summary>
    private static readonly string[] WebhookCodes =
    [
        "Integration.Webhook.View",
        "Integration.Webhook.Manage",
    ];

    /// <summary>The seven codes of TEL-B-18's table, verbatim.</summary>
    private static readonly string[] TelephonyCodes =
    [
        "Telephony.Call.Make",
        "Telephony.Call.View",
        "Telephony.Call.Link",
        "Telephony.Recording.Listen",
        "Telephony.Recording.Export",
        "Telephony.Stats.View",
        "Telephony.Settings.Manage",
    ];

    /// <summary>The seven codes of ASS-11's table, verbatim, plus the two departures.</summary>
    private static readonly string[] InsuranceCodes =
    [
        "Ins.Product.Manage",

        // The eighth, where ASS-11's table names seven. Listed with its reason so that comparing
        // this file to the specification raises a question and not a defect: ASS-03's endpoints all
        // sat behind Ins.Product.Manage, so an agent holding only Ins.Policy.Subscribe could
        // subscribe a policy without being able to see which products exist or what one costs —
        // and ASS-11's own purpose is to separate DISTRIBUTION from management, which puts reading
        // the catalogue on the distributor's side. See Permissions.CanViewInsuranceProduct.
        "Ins.Product.View",

        "Ins.Policy.Subscribe",
        "Ins.Policy.View",
        "Ins.Claim.Declare",
        "Ins.Claim.View",
        "Ins.Statement.View",

        // The NINTH, and the second departure. Finalising a bordereau is not a read: it freezes
        // the figures the institution stands behind and takes the month permanently out of reach
        // of the nightly generation, so a late premium can never be added to it again. Gating it
        // on Ins.Statement.View would let anybody who may LOOK at a bordereau close one — the
        // same mistake as letting a reader of the policy list subscribe a policy, which this
        // module already refuses twice over (Policy.View/Subscribe, Claim.View/Declare).
        // See Permissions.CanFinaliseInsuranceStatement.
        "Ins.Statement.Finalise",

        // The TENTH and ELEVENTH, and the third departure. The mécanisme de traitement des
        // réclamations of the règlement 01/CIMA/2024 is its own function with its own deadlines
        // and its own report to a supervisor, and the people who staff it at an IMF are not the
        // people who sell — folding it into Ins.Claim.View would mean a claims handler reading
        // every grievance about their own handling. Split read from write for the reason this
        // module already draws twice: a reader of the register must not answer on the
        // institution's behalf. See Permissions.CanViewInsuranceComplaint.
        "Ins.Complaint.View",
        "Ins.Complaint.Handle",

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
    public void The_seven_telephony_permissions_are_registered_in_Permissions_All()
    {
        var registered = Permissions.All.Select(p => p.Code).ToList();

        foreach (var code in TelephonyCodes)
            registered.Should().Contain(code, $"endpoints call RequireAuthorization(\"{code}\")");
    }

    [Fact]
    public void The_eleven_insurance_permissions_are_registered_in_Permissions_All()
    {
        var registered = Permissions.All.Select(p => p.Code).ToList();

        foreach (var code in InsuranceCodes)
            registered.Should().Contain(code, $"endpoints call RequireAuthorization(\"{code}\")");
    }

    [Fact]
    public void The_module_declares_no_permission_beyond_the_specification_tables()
    {
        var declared = Permissions.All
            .Where(p => p.Module == ApplicationModules.Integration)
            .Select(p => p.Code)
            .ToList();

        // Guards the other direction: a permission invented in passing would widen the surface
        // an administrator has to reason about, and nothing else would report it.
        declared.Should().BeEquivalentTo([.. IntegrationCodes, .. InsuranceCodes, .. TelephonyCodes, .. MessagingCodes, .. ConnectedAccountCodes, .. WebhookCodes]);
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
