namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The durable subscription state of ASS-05 — « Chaque étape est une commande distincte, chaînée
/// par événement ; l'état global de la souscription est consultable » — as a state machine.
///
/// <para>
/// Wave 2 writes the orchestration; this pins the shape it must drive, and in particular the one
/// terminal state nobody wants to model: the premium debited, the insurer refusing, and the
/// reversal failing too.
/// </para>
/// </summary>
public sealed class InsuranceSubscriptionSagaTests
{
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static InsuranceSubscription New()
        => InsuranceSubscription.Create(
            tenantId: Tenant,
            connectionId: Guid.NewGuid(),
            crmCustomerId: InsuranceTestHarness.Customer,
            productId: Guid.NewGuid(),
            effectiveDate: new DateOnly(2026, 6, 1),
            premiumAmount: 7_500m,
            currency: "XOF",
            createdBy: InsuranceTestHarness.Actor,
            clock: InsuranceTestHarness.Clock());

    [Fact]
    public void A_new_subscription_waits_for_its_debit_and_nothing_has_moved()
    {
        var subscription = New();

        subscription.Status.Should().Be(SubscriptionStatus.PendingDebit);
        subscription.CbsDebitReference.Should().BeNull();
        subscription.IsCancellable.Should().BeTrue();
        subscription.OwesRefund.Should().BeFalse();
    }

    [Fact]
    public void The_happy_path_is_debit_then_subscribe_then_issue()
    {
        var clock = InsuranceTestHarness.Clock();
        var subscription = New();
        var policyId = Guid.NewGuid();

        subscription.AttachDebitCommand(Guid.NewGuid(), clock);
        subscription.MarkDebited("CBS-TRX-7781", clock);

        // Debited is the one state in which money has moved and no policy exists. ASS-05's order
        // is not negotiable: the premium leaves first.
        subscription.Status.Should().Be(SubscriptionStatus.Debited);
        subscription.CbsDebitReference.Should().Be("CBS-TRX-7781");
        subscription.IsCancellable.Should().BeFalse();

        subscription.AttachSubscribeCommand(Guid.NewGuid(), clock);
        subscription.MarkIssued(policyId, "POL-2026-0042", clock);

        subscription.Status.Should().Be(SubscriptionStatus.Issued);
        subscription.PolicyId.Should().Be(policyId);
        subscription.InsurerPolicyReference.Should().Be("POL-2026-0042");
        subscription.OwesRefund.Should().BeFalse();
    }

    [Fact]
    public void A_debit_with_no_CBS_reference_is_refused_outright()
    {
        var subscription = New();

        // The guard that matters most in this aggregate. ReverseDebitAsync can only be keyed on
        // the reference the original debit returned, and ASS-10's statement prints it; accepting a
        // debit without one would leave the customer's money with no handle on it and no line to
        // justify it.
        var act = () => subscription.MarkDebited("  ", InsuranceTestHarness.Clock());

        act.Should().Throw<DomainException>().WithMessage("*cannot be reversed*");
        subscription.Status.Should().Be(SubscriptionStatus.PendingDebit);
    }

    [Fact]
    public void A_refused_debit_is_terminal_and_nothing_reached_the_insurer()
    {
        var clock = InsuranceTestHarness.Clock();
        var subscription = New();

        subscription.AttachDebitCommand(Guid.NewGuid(), clock);
        subscription.MarkDebitFailed("INTEGRATION_INSUFFICIENT_FUNDS", "solde 2 300 XOF", clock);

        // ASS-05, criterion 3: the subscription is NOT sent and the agent is told. The cheapest
        // failure there is, and it must stay distinguishable from the two that follow.
        subscription.Status.Should().Be(SubscriptionStatus.DebitFailed);
        subscription.FailureCode.Should().Be("INTEGRATION_INSUFFICIENT_FUNDS");
        subscription.OwesRefund.Should().BeFalse();
        subscription.SubscribeCommandId.Should().BeNull();
    }

    [Fact]
    public void A_refused_subscription_that_is_refunded_ends_clean()
    {
        var clock = InsuranceTestHarness.Clock();
        var subscription = New();

        subscription.AttachDebitCommand(Guid.NewGuid(), clock);
        subscription.MarkDebited("CBS-TRX-7781", clock);
        subscription.AttachSubscribeCommand(Guid.NewGuid(), clock);
        subscription.BeginReversal(Guid.NewGuid(), "INTEGRATION_REJECTED", "âge hors limites", clock);

        subscription.Status.Should().Be(SubscriptionStatus.Reversing);
        subscription.ReversalAttempts.Should().Be(1);

        subscription.MarkReversed("CBS-TRX-7782", clock);

        subscription.Status.Should().Be(SubscriptionStatus.Reversed);
        subscription.CbsReversalReference.Should().Be("CBS-TRX-7782");
        subscription.OwesRefund.Should().BeFalse();
    }

    /// <summary>
    /// The state nobody wants to model, and the reason it is a named state.
    ///
    /// <para>
    /// <b>The failure mode this test guards is a query, not a transition.</b> "The last command is
    /// Rejected" is true of this subscription AND of the cleanly refunded one above — identical on
    /// every command row — so a listing written that way cannot tell the institution what it owes.
    /// <see cref="SubscriptionStatus.PremiumRefundDue"/> and
    /// <see cref="InsuranceSubscription.OwesRefund"/> are what make that one predicate, served by
    /// <c>ix_ins_subscription_tenant_status</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void A_failed_reversal_leaves_a_NAMED_money_owing_state()
    {
        var clock = InsuranceTestHarness.Clock();
        var refunded = New();
        var owing = New();

        foreach (var subscription in new[] { refunded, owing })
        {
            subscription.AttachDebitCommand(Guid.NewGuid(), clock);
            subscription.MarkDebited("CBS-TRX-7781", clock);
            subscription.AttachSubscribeCommand(Guid.NewGuid(), clock);
            subscription.BeginReversal(Guid.NewGuid(), "INTEGRATION_REJECTED", "refus assureur", clock);
        }

        refunded.MarkReversed("CBS-TRX-7782", clock);
        owing.MarkRefundDue("INTEGRATION_ACCOUNT_NOT_OPERABLE", "compte clôturé", clock);

        owing.Status.Should().Be(SubscriptionStatus.PremiumRefundDue);
        owing.OwesRefund.Should().BeTrue();

        // The two outcomes are distinguishable, which is the point: the customer of `owing` has
        // been charged for a policy that does not exist.
        refunded.OwesRefund.Should().BeFalse();
        owing.CbsDebitReference.Should().Be("CBS-TRX-7781");
    }

    [Fact]
    public void A_refund_due_subscription_can_be_re_attempted_and_counts_its_attempts()
    {
        var clock = InsuranceTestHarness.Clock();
        var subscription = New();

        subscription.AttachDebitCommand(Guid.NewGuid(), clock);
        subscription.MarkDebited("CBS-TRX-7781", clock);
        subscription.AttachSubscribeCommand(Guid.NewGuid(), clock);
        subscription.BeginReversal(Guid.NewGuid(), "INTEGRATION_REJECTED", null, clock);
        subscription.MarkRefundDue("INTEGRATION_UNAVAILABLE", "CBS indisponible", clock);

        // Re-attempting is a HUMAN decision, not an automatic loop: the reversal already spent the
        // dispatcher's retry budget, and looping back would hide a failing CBS behind a row that
        // reads "in progress" forever. The counter is what tells an operator the CBS has already
        // refused once.
        subscription.BeginReversal(Guid.NewGuid(), "INTEGRATION_REJECTED", null, clock);

        subscription.Status.Should().Be(SubscriptionStatus.Reversing);
        subscription.ReversalAttempts.Should().Be(2);
    }

    [Fact]
    public void A_reversal_cannot_begin_without_the_original_debit_reference()
    {
        var subscription = New();
        var clock = InsuranceTestHarness.Clock();

        // Not reachable through the happy path — MarkDebited requires the reference — so this
        // pins the aggregate against a future caller that sets Debited by another route.
        var act = () => subscription.BeginReversal(Guid.NewGuid(), "X", null, clock);

        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(nameof(SubscriptionStatus.Issued))]
    [InlineData(nameof(SubscriptionStatus.Reversed))]
    public void A_settled_subscription_refuses_every_further_step(string _)
    {
        var clock = InsuranceTestHarness.Clock();
        var subscription = New();

        subscription.AttachDebitCommand(Guid.NewGuid(), clock);
        subscription.MarkDebited("CBS-TRX-7781", clock);
        subscription.AttachSubscribeCommand(Guid.NewGuid(), clock);
        subscription.MarkIssued(Guid.NewGuid(), "POL-1", clock);

        // THROWS rather than returning a Result, like IntegrationCommand's transition table: a
        // step firing out of order is a defect in the chaining, and swallowing it would let a
        // redelivered "debit succeeded" event mark an issued subscription as debited again.
        ((Action)(() => subscription.MarkDebited("CBS-TRX-9999", clock)))
            .Should().Throw<DomainException>();

        ((Action)(() => subscription.Cancel("trop tard", clock)))
            .Should().Throw<DomainException>();

        ((Action)(() => subscription.MarkIssued(Guid.NewGuid(), "POL-2", clock)))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Cancelling_is_only_possible_before_the_money_moves()
    {
        var clock = InsuranceTestHarness.Clock();
        var cancelled = New();

        cancelled.Cancel("le client a renoncé", clock);
        cancelled.Status.Should().Be(SubscriptionStatus.Cancelled);

        var debited = New();
        debited.AttachDebitCommand(Guid.NewGuid(), clock);
        debited.MarkDebited("CBS-TRX-7781", clock);

        // Once money has moved there is nothing to cancel, only something to refund — so the
        // route out is a reversal and never a cancellation that would leave the premium taken and
        // the row looking abandoned.
        ((Action)(() => debited.Cancel("trop tard", clock))).Should().Throw<DomainException>();
    }

    [Fact]
    public void A_subscription_refuses_a_non_positive_premium_and_a_missing_currency()
    {
        var clock = InsuranceTestHarness.Clock();

        ((Action)(() => InsuranceSubscription.Create(
                Tenant, Guid.NewGuid(), InsuranceTestHarness.Customer, Guid.NewGuid(),
                new DateOnly(2026, 6, 1), 0m, "XOF", InsuranceTestHarness.Actor, clock)))
            .Should().Throw<DomainException>();

        ((Action)(() => InsuranceSubscription.Create(
                Tenant, Guid.NewGuid(), InsuranceTestHarness.Customer, Guid.NewGuid(),
                new DateOnly(2026, 6, 1), 7_500m, " ", InsuranceTestHarness.Actor, clock)))
            .Should().Throw<DomainException>();
    }
}
