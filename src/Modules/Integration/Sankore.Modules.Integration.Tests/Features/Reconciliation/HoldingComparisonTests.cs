namespace Sankore.Modules.Integration.Tests.Features.Reconciliation;

using FluentAssertions;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Reconciliation;
using Xunit;

/// <summary>
/// BNF-B-17's comparison rules for a holding, as pure functions — the split every sweep in this
/// module uses, so the decisions are pinned without a job, a container or a database.
/// </summary>
public sealed class HoldingComparisonTests
{
    private static HoldingSyncState Crm(
        string status = "Active", int clauseVersion = 1, string? externalRef = "POL-00042")
        => new(
            HoldingId: Guid.NewGuid(),
            Status: status,
            ExternalRef: externalRef,
            ClauseVersion: clauseVersion,
            IsLive: status == "Active");

    // ── the two sides agree ─────────────────────────────────────────────────

    [Fact]
    public void A_live_holding_the_insurer_confirms_with_the_current_clause_is_clean()
    {
        ReconciliationComparison.CompareHolding(Crm(), policyIsLive: true, currentClauseSent: true)
            .Should().BeNull();
    }

    [Fact]
    public void A_closed_holding_the_insurer_never_knew_is_clean()
    {
        // Both sides agree nothing is in force. A subscription that never happened is the ordinary
        // end of a draft, not a divergence.
        ReconciliationComparison.CompareHolding(
                Crm("Closed"), policyIsLive: null, currentClauseSent: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_closed_holding_whose_policy_is_also_closed_is_clean()
    {
        ReconciliationComparison.CompareHolding(
                Crm("Closed"), policyIsLive: false, currentClauseSent: true)
            .Should().BeNull();
    }

    // ── the insurer never confirmed it ──────────────────────────────────────

    [Fact]
    public void A_live_holding_the_insurer_does_not_know_is_missing_externally()
    {
        // The type that catches a subscription whose command was lost — what the story is for.
        var divergence = ReconciliationComparison.CompareHolding(
            Crm(), policyIsLive: null, currentClauseSent: true);

        divergence.Should().NotBeNull();
        divergence!.GapType.Should().Be(GapType.MissingInExternal);
        divergence.DetailsJson.Should().Contain("\"presentInExternal\":false");
    }

    [Fact]
    public void A_missing_policy_takes_precedence_over_an_unsent_clause()
    {
        // One holding yields at most one gap, and the remedy here is the subscription rather than
        // the avenant — reporting both would count one contract twice in the summary.
        var divergence = ReconciliationComparison.CompareHolding(
            Crm(clauseVersion: 3), policyIsLive: null, currentClauseSent: false);

        divergence!.GapType.Should().Be(GapType.MissingInExternal);
    }

    // ── the two sides disagree about force ──────────────────────────────────

    [Fact]
    public void A_live_holding_against_a_cancelled_policy_is_a_status_mismatch()
    {
        var divergence = ReconciliationComparison.CompareHolding(
            Crm(), policyIsLive: false, currentClauseSent: true);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
        divergence.DetailsJson.Should().Contain("\"liveInCrm\":true");
        divergence.DetailsJson.Should().Contain("\"liveInExternal\":false");
    }

    [Fact]
    public void A_closed_holding_against_a_live_policy_is_a_status_mismatch_too()
    {
        // The direction that matters most: the CRM thinks the client has nothing and the insurer
        // is still charging a premium.
        var divergence = ReconciliationComparison.CompareHolding(
            Crm("Closed"), policyIsLive: true, currentClauseSent: true);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
    }

    [Fact]
    public void A_status_mismatch_takes_precedence_over_an_unsent_clause()
    {
        var divergence = ReconciliationComparison.CompareHolding(
            Crm(clauseVersion: 2), policyIsLive: false, currentClauseSent: false);

        divergence!.GapType.Should().Be(GapType.StatusMismatch);
    }

    // ── the clause the insurer was never sent ───────────────────────────────

    [Fact]
    public void A_live_holding_whose_current_clause_never_left_is_out_of_sync()
    {
        // An avenant recorded while the insurer was unreachable, or one whose command was rejected
        // and never re-sent. It matters: the policy would pay the previous beneficiaries.
        var divergence = ReconciliationComparison.CompareHolding(
            Crm(clauseVersion: 2), policyIsLive: true, currentClauseSent: false);

        divergence!.GapType.Should().Be(GapType.DesignationOutOfSync);
        divergence.DetailsJson.Should().Contain("\"crmClauseVersion\":2");
        divergence.DetailsJson.Should().Contain("\"currentClauseSent\":false");
    }

    [Fact]
    public void A_CLOSED_holding_whose_last_avenant_never_left_is_not_reported()
    {
        // History with no remedy. Reporting it would fill the ledger with findings nobody can act
        // on, which is how a ledger stops being read.
        ReconciliationComparison.CompareHolding(
                Crm("Closed"), policyIsLive: false, currentClauseSent: false)
            .Should().BeNull();
    }

    // ── a reference pointing at nothing ─────────────────────────────────────

    [Fact]
    public void A_reference_with_no_holding_behind_it_is_not_a_gap()
    {
        // M01 deletes no holding — closing is a status — so this is a data-integrity anomaly
        // rather than a reconciliation finding. Wiring it to MissingInCrm would give that gap type
        // a detector that in practice never fires, which is the same lie in a different shape.
        ReconciliationComparison.CompareHolding(
                crm: null, policyIsLive: null, currentClauseSent: false)
            .Should().BeNull();
    }

    [Fact]
    public void A_holding_still_in_a_queue_is_not_yet_a_divergence()
    {
        // PendingSync is not in force yet, so the insurer legitimately knows nothing about it. A
        // comparison that treated it as live would report every subscription in flight.
        ReconciliationComparison.CompareHolding(
                Crm("PendingSync"), policyIsLive: null, currentClauseSent: true)
            .Should().BeNull();
    }

    [Fact]
    public void A_rejected_holding_is_not_a_divergence_either()
    {
        ReconciliationComparison.CompareHolding(
                Crm("Rejected"), policyIsLive: null, currentClauseSent: false)
            .Should().BeNull();
    }
}
