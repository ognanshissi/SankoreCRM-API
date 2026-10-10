namespace Sankore.Modules.Integration.Tests.Adapters.PerfectVision;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.PerfectVision;
using Sankore.Modules.Integration.Domain;
using Xunit;

/// <summary>
/// INT-28, criterion 2 — « la lecture de solde passe par la vue SQL en lecture seule si elle
/// existe, sinon par le snapshot ».
///
/// <para>
/// The branch is pinned on its own, with no adapter, no database and no tenant, because it is the
/// only half of criterion 2 that does not wait on the vendor: the decision is ours, the query is
/// theirs. These tests keep passing unchanged the day the specification arrives, which is the
/// point — a routing rule that had to be re-asserted alongside a new parser would be a rule
/// nobody trusts.
/// </para>
///
/// <para>
/// The view names below are obvious placeholders. No real Perfect Vision view name appears
/// anywhere in this chantier: the condition under test is "is a name configured", and a plausible
/// one would be the first invented vendor identifier in the repository.
/// </para>
/// </summary>
public sealed class PerfectVisionBalanceRoutingTests
{
    [Fact]
    public void A_configured_view_name_routes_the_read_to_the_view()
    {
        var settings = new PerfectVisionSettings { BalanceViewName = "PLACEHOLDER_VIEW" };

        PerfectVisionBalanceRouting.ChooseFor(settings)
            .Should().Be(PerfectVisionBalanceSource.ReadOnlyView);

        PerfectVisionBalanceRouting.HasReadOnlyView(settings).Should().BeTrue();
    }

    [Fact]
    public void No_view_name_falls_back_to_the_snapshot()
    {
        var settings = new PerfectVisionSettings();

        settings.BalanceViewName.Should().BeNull("an installation without a view configures none");

        PerfectVisionBalanceRouting.ChooseFor(settings)
            .Should().Be(PerfectVisionBalanceSource.Snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void A_blank_view_name_is_an_absent_one(string blank)
    {
        // A view name arrives through INT-03's settings form, and a cleared field posts a blank
        // rather than a null. Reading that as a name would declare a live-balance capability
        // pointing at nothing — a button offered on a screen that answers an error.
        PerfectVisionBalanceRouting.ChooseFor(new PerfectVisionSettings { BalanceViewName = blank })
            .Should().Be(PerfectVisionBalanceSource.Snapshot);
    }

    [Fact]
    public void Absent_settings_route_to_the_snapshot_rather_than_throwing()
    {
        // The caller may be the capability matrix of a tenant that has configured nothing at all.
        // "There is no view" is the right answer to that; an exception would make a screen that
        // merely asks what is supported fail.
        PerfectVisionBalanceRouting.ChooseFor(null)
            .Should().Be(PerfectVisionBalanceSource.Snapshot);
    }

    [Fact]
    public void The_batch_fields_of_the_connection_do_not_decide_the_balance_source()
    {
        // BatchCapableSettings carries a cut-off, directories and an SFTP host. A fully configured
        // batch cycle still reads balances from the snapshot: the file cycle and the view are
        // independent, and conflating them would hand a counter a figure from last night's file
        // while calling it live.
        var batchConfigured = new PerfectVisionSettings
        {
            CutOffTime = new TimeOnly(18, 30),
            OutboundDirectory = "/out",
            InboundDirectory = "/in",
            SftpHost = "sftp.example.invalid",
        };

        PerfectVisionBalanceRouting.ChooseFor(batchConfigured)
            .Should().Be(PerfectVisionBalanceSource.Snapshot);
    }
}
