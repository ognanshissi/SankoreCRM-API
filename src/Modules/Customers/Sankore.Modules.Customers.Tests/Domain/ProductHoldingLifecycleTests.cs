namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The life of a holding (BNF-B-14), and the signature that gates everything leaving the CRM
/// (BNF-B-16).
/// </summary>
public sealed class ProductHoldingLifecycleTests
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private ProductHolding Draft() => TestHoldingFactory.Draft(
        _tenant, _client, TestHoldingFactory.Line(roles: [TestHoldingFactory.Proxy]), _actor, Now);

    /// <summary>Takes a holding as far as PendingSync, the way an agent would.</summary>
    private ProductHolding Submitted()
    {
        var holding = Draft();
        holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();
        holding.Sign("doc://signature/1", Now, Now).IsSuccess.Should().BeTrue();
        holding.SubmitForSync(Now).IsSuccess.Should().BeTrue();
        return holding;
    }

    // ── the happy path ──────────────────────────────────────────────────────

    [Fact]
    public void A_subscription_starts_as_an_editable_draft()
    {
        var holding = Draft();

        holding.Status.Should().Be(HoldingStatus.Draft);
        holding.Origin.Should().Be(HoldingOrigin.CrmSubscription);
        holding.IsEditable.Should().BeTrue();
        holding.IsReadOnly.Should().BeFalse();
        holding.ClauseVersion.Should().Be(1);
    }

    [Fact]
    public void It_walks_draft_to_active()
    {
        var holding = Submitted();

        holding.ConfirmSync("Orass", "POL-00042", Now).IsSuccess.Should().BeTrue();

        holding.Status.Should().Be(HoldingStatus.Active);
        holding.ExternalRef.Should().Be("POL-00042");
        holding.ExternalSystem.Should().Be("Orass");
        holding.SyncedAt.Should().Be(Now);
    }

    // ── insurance lot 2: accepted, not yet paid ─────────────────────────────

    [Fact]
    public void A_contract_sent_before_its_premium_waits_for_it_before_coming_into_force()
    {
        var holding = Submitted();

        holding.ConfirmSync("Orass", "POL-7", Now, awaitingPayment: true).IsSuccess.Should().BeTrue();
        holding.Status.Should().Be(HoldingStatus.AwaitingPayment);
        holding.ExternalRef.Should().Be("POL-7");

        holding.ConfirmPayment(Now).IsSuccess.Should().BeTrue();
        holding.Status.Should().Be(HoldingStatus.Active);
    }

    // ── the signature is the gate (BNF-B-16) ────────────────────────────────

    [Fact]
    public void Nothing_reaches_a_back_office_unsigned()
    {
        var holding = Draft();
        holding.RequestSignature([], Now);

        var result = holding.SubmitForSync(Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.SignatureRequired);
        holding.Status.Should().Be(HoldingStatus.PendingSignature, "the state did not move");
    }

    [Fact]
    public void A_signature_with_no_evidence_is_refused()
    {
        var holding = Draft();
        holding.RequestSignature([], Now);

        holding.Sign("   ", Now, Now).Error.Should().Be(CustomerErrors.SignatureReferenceRequired);
        holding.SignedAt.Should().BeNull();
    }

    [Fact]
    public void A_correction_clears_the_signature()
    {
        // What the client signed is no longer what would be sent. Keeping it would let corrected
        // terms reach a back office carrying evidence of consent to the previous ones.
        var holding = Submitted();
        holding.RecordRejection("Capital above the line's ceiling", Now);

        holding.ReturnToDraft(Now).IsSuccess.Should().BeTrue();

        holding.Status.Should().Be(HoldingStatus.Draft);
        holding.SignedAt.Should().BeNull();
        holding.SignatureReference.Should().BeNull();
    }

    // ── a required role blocks the signature, not the claim ─────────────────

    [Fact]
    public void A_file_missing_a_required_designation_cannot_be_sent_for_signature()
    {
        // Before the client signs, not after: an insurer that refuses the contract once it is
        // signed has cost the branch a visit and the client their trust.
        var holding = Draft();

        var result = holding.RequestSignature(["BENEFICIAIREDECES"], Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.DesignationRequiredRoleMissing);
        holding.Status.Should().Be(HoldingStatus.Draft);
    }

    // ── the state machine refuses what it has to ────────────────────────────

    [Fact]
    public void An_active_holding_cannot_be_re_signed()
    {
        var holding = Submitted();
        holding.ConfirmSync("Orass", "POL-1", Now);

        holding.Sign("doc://signature/2", Now, Now).Error
            .Should().Be(CustomerErrors.HoldingInvalidTransition);
    }

    [Fact]
    public void A_queued_holding_cannot_go_back_for_a_second_signature()
    {
        // Once it has been handed to a back office the request is out of our hands, and re-signing
        // would produce a second subscription downstream.
        var holding = Submitted();

        holding.ReturnToDraft(Now).Error.Should().Be(CustomerErrors.HoldingInvalidTransition);
    }

    [Fact]
    public void Changing_the_terms_after_the_signature_clears_it()
    {
        // Found in acceptance: the date of effect was moved after the client signed, and the
        // contract could still be sent on the old signature.
        var holding = Draft();
        holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();
        holding.Sign("doc://signature/1", Now, Now).IsSuccess.Should().BeTrue();

        holding.UpdateSpecifics(
            HoldingSpecifics.Create(1_000_000m, null, null, null, null, null, "XOF", new DateOnly(2026, 11, 1), null).Value,
            Now).IsSuccess.Should().BeTrue();

        holding.SignedAt.Should().BeNull();
        holding.SignatureReference.Should().BeNull();
        holding.SubmitForSync(Now).Error.Should().Be(CustomerErrors.SignatureRequired);
    }

    [Fact]
    public void Saving_the_same_terms_keeps_the_signature()
    {
        var holding = Draft();
        holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();
        holding.Sign("doc://signature/1", Now, Now).IsSuccess.Should().BeTrue();

        holding.UpdateSpecifics(holding.Specifics, Now).IsSuccess.Should().BeTrue();

        holding.SignedAt.Should().NotBeNull();
    }

    [Fact]
    public void Terms_are_not_editable_once_the_holding_is_live()
    {
        var holding = Submitted();
        holding.ConfirmSync("Orass", "POL-1", Now);

        var result = holding.UpdateSpecifics(
            HoldingSpecifics.Create(1_000_000m, null, null, null, null, null, "XOF", null, null).Value,
            Now);

        result.Error.Should().Be(CustomerErrors.HoldingNotEditable);
    }

    [Fact]
    public void Closing_needs_a_reason_and_closes_the_clause_with_the_contract()
    {
        var line = TestHoldingFactory.Line();
        var holding = TestHoldingFactory.Draft(_tenant, _client, line, _actor, Now);
        holding.Designate([TestHoldingFactory.Beneficiary(100m)], line.AllowedRoles, _actor, Now);

        holding.Close("  ", Now).Error.Should().Be(CustomerErrors.ReasonRequired);

        holding.Close("Surrendered by the client", Now).IsSuccess.Should().BeTrue();
        holding.Status.Should().Be(HoldingStatus.Closed);

        // Closed and not deleted: a claim filed later asks who was designated at the time.
        holding.CurrentDesignations.Should().BeEmpty();
        holding.Designations.Should().ContainSingle()
               .Which.Status.Should().Be(DesignationStatus.Closed);
    }

    [Fact]
    public void A_closed_holding_goes_nowhere()
    {
        var holding = Draft();
        holding.Close("Cancelled before signature", Now);

        holding.RequestSignature([], Now).Error
            .Should().Be(CustomerErrors.HoldingInvalidTransition);
    }

    [Fact]
    public void A_rejection_needs_a_reason_an_agent_can_act_on()
    {
        var holding = Submitted();

        holding.RecordRejection("   ", Now).Error.Should().Be(CustomerErrors.ReasonRequired);

        holding.RecordRejection("Beneficiary under 18 without a guardian", Now)
               .IsSuccess.Should().BeTrue();

        holding.Status.Should().Be(HoldingStatus.Rejected);
        holding.RejectionReason.Should().Be("Beneficiary under 18 without a guardian");
    }

    [Fact]
    public void Confirming_a_sync_without_the_back_offices_reference_is_refused()
    {
        var holding = Submitted();

        holding.ConfirmSync("Orass", "  ", Now).Error
            .Should().Be(CustomerErrors.ExternalRefRequired);
        holding.Status.Should().Be(HoldingStatus.PendingSync);
    }

    // ── what a screen may offer (BNF-B-20) ──────────────────────────────────

    [Fact]
    public void A_draft_offers_the_actions_a_draft_accepts()
    {
        var holding = Draft();

        holding.AvailableActions.Should().BeEquivalentTo(
            ["specifics", "signature-request", "closure", "designations"]);
    }

    [Fact]
    public void An_unsigned_holding_awaiting_signature_does_not_offer_submission()
    {
        // The button that would 409. BNF-B-20 exists so a screen never shows it.
        var holding = Draft();
        holding.RequestSignature([], Now);

        holding.AvailableActions.Should().Contain("signature");
        holding.AvailableActions.Should().NotContain("submission");
    }

    [Fact]
    public void Once_signed_it_does()
    {
        var holding = Draft();
        holding.RequestSignature([], Now);
        holding.Sign("doc://signature/1", Now, Now);

        holding.AvailableActions.Should().Contain("submission");
    }

    [Fact]
    public void A_queued_holding_offers_nothing_but_waiting()
    {
        // The request is in a back office's hands: no edit, no re-signature, and no clause change
        // — it would be sent as neither version.
        Submitted().AvailableActions.Should().BeEmpty();
    }

    [Fact]
    public void A_live_holding_offers_an_avenant_and_a_closure()
    {
        var holding = Submitted();
        holding.ConfirmSync("Orass", "POL-1", Now);

        holding.AvailableActions.Should().BeEquivalentTo(["closure", "designations"]);
    }

    [Fact]
    public void A_rejected_holding_offers_a_correction()
    {
        var holding = Submitted();
        holding.RecordRejection("Capital above the ceiling", Now);

        holding.AvailableActions.Should().Contain("return-to-draft");
        holding.AvailableActions.Should().Contain("designations");
    }

    [Fact]
    public void A_closed_one_offers_nothing()
    {
        var holding = Draft();
        holding.Close("Cancelled", Now);

        holding.AvailableActions.Should().BeEmpty();
    }

    [Fact]
    public void Every_offered_action_is_one_the_aggregate_actually_accepts()
    {
        // Guards the real risk in a computed list: it drifting from the methods it describes. The
        // check is per state, over every state a CRM holding can reach.
        var holding = Draft();
        holding.AvailableActions.Should().Contain("specifics");
        holding.UpdateSpecifics(HoldingSpecifics.None, Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().Contain("signature-request");
        holding.RequestSignature([], Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().Contain("signature");
        holding.Sign("doc://signature/1", Now, Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().Contain("submission");
        holding.SubmitForSync(Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().BeEmpty();
        holding.ConfirmSync("Orass", "POL-1", Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().Contain("closure");
        holding.Close("Surrendered", Now).IsSuccess.Should().BeTrue();

        holding.AvailableActions.Should().BeEmpty();
    }

    // ── an imported holding is read-only (BNF-B-14) ─────────────────────────

    [Fact]
    public void A_holding_projected_from_a_back_office_refuses_every_change()
    {
        // The authoritative copy lives elsewhere, so a CRM edit would be silently overwritten by
        // the next reconciliation and the agent would never learn their change was lost.
        var line = TestHoldingFactory.Line();

        var imported = ProductHolding.Imported(
            _tenant, _client, line, HoldingSpecifics.None, "Temenos", "ACC-991", _actor, Now);

        imported.IsReadOnly.Should().BeTrue();
        imported.IsEditable.Should().BeFalse();
        imported.Status.Should().Be(HoldingStatus.Active);

        imported.UpdateSpecifics(HoldingSpecifics.None, Now).Error
            .Should().Be(CustomerErrors.HoldingReadOnly);

        imported.Designate([TestHoldingFactory.Beneficiary(100m)], line.AllowedRoles, _actor, Now)
                .Error.Should().Be(CustomerErrors.HoldingReadOnly);

        imported.Close("whatever", Now).Error.Should().Be(CustomerErrors.HoldingReadOnly);

        imported.AvailableActions.Should().BeEmpty(
            "a screen must not offer a button on a holding the CRM may not change (BNF-B-20)");
    }

    [Fact]
    public void An_imported_holding_needs_the_back_offices_reference()
    {
        var act = () => ProductHolding.Imported(
            _tenant, _client, TestHoldingFactory.Line(), HoldingSpecifics.None,
            "Temenos", "  ", _actor, Now);

        act.Should().Throw<DomainException>();
    }

    // ── specifics ───────────────────────────────────────────────────────────

    [Fact]
    public void A_rate_typed_as_1200_instead_of_1200_percent_is_refused()
    {
        // The mistake that reaches a back office looking plausible.
        HoldingSpecifics.Create(null, null, null, null, 1200m, null, null, null, null)
            .Error.Should().Be("HOLDING_SPECIFICS_INVALID_RATE");
    }

    [Theory]
    [InlineData("monthly", "Monthly")]
    [InlineData(" SemiAnnual ", "SemiAnnual")]
    [InlineData("SINGLE", "Single")]
    public void A_known_periodicity_is_stored_in_its_canonical_spelling(string typed, string stored)
        => HoldingSpecifics.Create(null, 25_000m, typed, null, null, null, null, null, null)
            .Value.Periodicity.Should().Be(stored);

    [Fact]
    public void A_periodicity_outside_the_list_is_refused()
    {
        // The field used to be free text: « Mensuelle » was saved as typed and read by nobody.
        HoldingSpecifics.Create(null, 25_000m, "Mensuelle", null, null, null, null, null, null)
            .Error.Should().Be("HOLDING_SPECIFICS_INVALID_PERIODICITY");
    }

    [Fact]
    public void A_negative_amount_is_refused_and_a_currency_is_normalised()
    {
        HoldingSpecifics.Create(-1m, null, null, null, null, null, null, null, null)
            .Error.Should().Be("HOLDING_SPECIFICS_INVALID_AMOUNT");

        HoldingSpecifics.Create(null, null, null, null, null, null, " xof ", null, null)
            .Value.Currency.Should().Be("XOF");
    }
}
