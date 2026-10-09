namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The comparison rules of INT-34, pinned without a job or a database.
/// </summary>
public sealed class ReconciliationComparisonTests
{
    private static readonly Guid Customer = new("dddddddd-0000-0000-0000-00000000000d");

    [Fact]
    public void An_active_client_the_external_system_does_not_hold_is_MissingInExternal()
    {
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer),
            snapshotExists: false,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: null);

        divergence!.GapType.Should().Be(GapType.MissingInExternal);
        divergence.DetailsJson.Should().Contain("\"presentInExternal\":false");
    }

    [Fact]
    public void An_inactive_client_the_external_system_does_not_hold_is_not_a_gap()
    {
        // Both sides agree that the customer is out of service. Opening a gap here would fill the
        // ledger with every customer either side has ever retired, which is the failure mode the
        // aggregate's own doc comment warns about: a report nobody can read is not a report.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer, status: "Archived"),
            snapshotExists: false,
            crmKycLevel: null,
            cbsKycLevel: null);

        divergence.Should().BeNull();
    }

    [Theory]
    [InlineData("PendingKyc")]
    [InlineData("Suspended")]
    [InlineData("KycRejected")]
    [InlineData("Archived")]
    [InlineData("Merged")]
    public void A_client_the_CRM_does_not_consider_active_but_the_external_system_holds_is_StatusMismatch(
        string status)
    {
        // The single definition of "active in the CRM" is Status == "Active", reproducing
        // ICustomersModule.ExistsAndActiveAsync. Every other ClientStatus value is therefore a
        // divergence when the external system still carries the customer — if this test ever
        // fails for a NEW status M01 adds, that is the signal to decide about it deliberately
        // rather than discovering it in production.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer, status: status),
            snapshotExists: true,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: KycLevel.Full);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
        divergence.DetailsJson.Should().Contain($"\"crmStatus\":\"{status}\"");
    }

    [Fact]
    public void A_merged_client_is_reported_as_a_status_divergence_and_flagged_as_merged()
    {
        // Deliberate, not accidental: the external system is addressing a record M01 has retired
        // in favour of a survivor, so the remedy is to re-point the reference rather than to
        // reactivate anything — and the flag is what tells the operator that without naming the
        // survivor in an exportable report.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer, status: "Merged", mergedIntoId: Guid.NewGuid()),
            snapshotExists: true,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: KycLevel.Full);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
        divergence.DetailsJson.Should().Contain("\"merged\":true");
    }

    [Fact]
    public void A_status_divergence_takes_precedence_over_a_tier_divergence()
    {
        // One customer yields at most one gap. Both conditions hold here; reporting both would
        // count the same person twice in the summary an administrator reads, and a tier mismatch
        // on a record that should not be operable is noise in front of the finding that matters.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer, status: "Suspended"),
            snapshotExists: true,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: KycLevel.Simplified);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
    }

    [Fact]
    public void Disagreeing_tiers_on_an_active_client_are_a_KycMismatch_carrying_both_values()
    {
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer),
            snapshotExists: true,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: KycLevel.Simplified);

        divergence!.GapType.Should().Be(GapType.KycMismatch);
        divergence.DetailsJson.Should().Contain("\"crmKycLevel\":\"Full\"");
        divergence.DetailsJson.Should().Contain("\"externalKycLevel\":\"Simplified\"");
    }

    [Fact]
    public void An_unknown_external_tier_is_not_a_divergence()
    {
        // Reuses SnapshotKycLevels.Diverge, which already owns this judgement: a null CBS tier
        // means the CBS has never been told, and reporting it would queue every customer created
        // before a tier was ever pushed. If this ever fails, the two mechanisms that compare a
        // tier have stopped agreeing on what a divergence is.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer),
            snapshotExists: true,
            crmKycLevel: KycLevel.Full,
            cbsKycLevel: null);

        divergence.Should().BeNull();
    }

    [Fact]
    public void An_unreadable_CRM_tier_produces_no_gap_rather_than_a_guess()
    {
        // crmKycLevel is null only when M02 could not be asked. "We could not find out" must not
        // become a finding an officer has to clear.
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer),
            snapshotExists: true,
            crmKycLevel: null,
            cbsKycLevel: KycLevel.Simplified);

        divergence.Should().BeNull();
    }

    [Fact]
    public void Agreement_on_both_axes_is_not_a_gap()
    {
        var divergence = ReconciliationComparison.Compare(
            ReconciliationTestContext.Client(Customer),
            snapshotExists: true,
            crmKycLevel: KycLevel.Simplified,
            cbsKycLevel: KycLevel.Simplified);

        divergence.Should().BeNull();
    }

    [Fact]
    public void No_detail_payload_carries_identity_data()
    {
        // The aggregate's contract: details hold compared VALUES because a reconciliation report
        // is routinely exported and mailed. The client fixture's name and number must appear in
        // none of the three shapes this class produces.
        var client = ReconciliationTestContext.Client(Customer, status: "Suspended");

        var details = new[]
        {
            ReconciliationComparison.Compare(
                ReconciliationTestContext.Client(Customer), false, KycLevel.Full, null)!.DetailsJson,
            ReconciliationComparison.Compare(client, true, KycLevel.Full, KycLevel.Full)!.DetailsJson,
            ReconciliationComparison.Compare(
                ReconciliationTestContext.Client(Customer), true, KycLevel.Full, KycLevel.Simplified)!
                .DetailsJson,
        };

        foreach (var payload in details)
        {
            payload.Should().NotContain(client.DisplayName);
            payload.Should().NotContain(client.ClientNumber);
            payload.Should().NotContain(Customer.ToString());
        }
    }

    [Fact]
    public void The_CRM_active_rule_is_an_exact_match_on_the_M01_status_name()
    {
        // Ordinal and exact: the string is ClientStatus.ToString() on the other side of the
        // contract, so a case difference means M01 changed how it serialises its own enum — which
        // must surface here as a failure rather than be absorbed into "close enough".
        ReconciliationComparison.IsActiveInCrm(
            ReconciliationTestContext.Client(Customer, status: "Active")).Should().BeTrue();

        ReconciliationComparison.IsActiveInCrm(
            ReconciliationTestContext.Client(Customer, status: "active")).Should().BeFalse();
    }

    [Fact]
    public void MissingInCrm_is_declared_undetectable_and_is_not_one_of_the_scanned_types()
    {
        // The visible half of design decision 1. If a later lot gains a full external portfolio
        // enumeration, moving the value between these two collections is the whole change — and
        // this test is what says the move was deliberate.
        ReconciliationScope.NotDetected.Should().BeEquivalentTo([GapType.MissingInCrm]);

        ReconciliationScope.Detected.Should().BeEquivalentTo(
            [GapType.MissingInExternal, GapType.KycMismatch, GapType.StatusMismatch]);

        // Every value of the enum is accounted for in exactly one of the two: a fifth gap type
        // added without a decision about whether it can be detected would fail here.
        ReconciliationScope.Detected.Concat(ReconciliationScope.NotDetected)
            .Should().BeEquivalentTo(Enum.GetValues<GapType>());
    }
}
