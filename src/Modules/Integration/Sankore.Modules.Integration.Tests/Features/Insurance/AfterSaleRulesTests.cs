namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Insurance lots 3 and 4: the insurer's answer, requests on a policy, the remittance.</summary>
public sealed class AfterSaleRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static InsuranceSubscription Sent(Clock clock, PremiumPaymentMode mode = PremiumPaymentMode.RecordedByAgent)
    {
        var saga = InsuranceSubscription.Prepare(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 7, 1), 50_000m, "XOF",
            Guid.NewGuid(), clock, holdingId: Guid.NewGuid(), clauseVersion: 1, paymentMode: mode, cbsAccountRef: null,
            paymentMethod: mode == PremiumPaymentMode.RecordedByAgent ? RecordedPaymentMethod.Cash : null,
            paymentReference: mode == PremiumPaymentMode.RecordedByAgent ? "REC-1" : null,
            paidOn: mode == PremiumPaymentMode.RecordedByAgent ? new DateOnly(2026, 6, 1) : null);

        if (mode == PremiumPaymentMode.RecordedByAgent) saga.StartWithRecordedPayment(clock);
        else saga.StartAwaitingPremium(clock);
        saga.AttachSubscribeCommand(Guid.NewGuid(), clock);
        return saga;
    }

    [Fact]
    public void The_insurers_silence_counts_only_after_15_days()
    {
        var clock = new Clock(Now);
        var saga = Sent(clock);
        saga.SentToInsurerAt.Should().Be(Now);

        clock.Now = Now.AddDays(14);
        var early = () => saga.RecordUnderwriting(UnderwritingOutcome.DeemedAcceptedBySilence, null, null, null, "Silence", new DateOnly(2026, 6, 15), Guid.NewGuid(), clock);
        early.Should().Throw<DomainException>();

        clock.Now = Now.AddDays(15);
        early.Should().NotThrow();
        saga.Underwriting.Should().Be(UnderwritingOutcome.DeemedAcceptedBySilence);
    }

    [Fact]
    public void A_surcharge_needs_the_clients_answer_then_its_collection()
    {
        var clock = new Clock(Now);
        var saga = Sent(clock);

        saga.RecordUnderwriting(UnderwritingOutcome.AcceptedWithSurcharge, 12_000m, null, null, "LETTRE-1", new DateOnly(2026, 6, 5), Guid.NewGuid(), clock);
        saga.AwaitsClientAnswer.Should().BeTrue();

        saga.RecordClientAnswer(ClientUnderwritingAnswer.Accepted, "AVENANT-1", clock);
        saga.IsSurchargeOutstanding.Should().BeTrue();

        saga.RecordLatePayment(RecordedPaymentMethod.MobileMoney, "OM-9", new DateOnly(2026, 6, 6), clock);
        saga.IsSurchargeOutstanding.Should().BeFalse();
    }

    [Theory]
    [InlineData(PremiumPaymentMode.RecordedByAgent, SubscriptionStatus.PremiumRefundDue)]
    [InlineData(PremiumPaymentMode.InsurerCollects, SubscriptionStatus.Refused)]
    public void A_refusal_after_sending_owes_back_only_what_was_collected(PremiumPaymentMode mode, SubscriptionStatus expected)
    {
        var clock = new Clock(Now);
        var saga = Sent(clock, mode);

        saga.RecordUnderwriting(UnderwritingOutcome.Refused, null, null, null, "LETTRE-2", new DateOnly(2026, 6, 5), Guid.NewGuid(), clock);
        saga.EndWithoutCover("INSURER_REFUSED", null, clock);

        saga.Status.Should().Be(expected);
    }

    [Fact]
    public void A_renunciations_refund_is_due_30_days_after_the_request()
    {
        var op = PolicyOperation.Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PolicyOperationKind.Renunciation,
            50_000m, "XOF", null, null, Guid.NewGuid(), new Clock(Now));

        op.RefundDueBy.Should().Be(new DateOnly(2026, 7, 1));

        var paidBeforeSending = () => op.MoveTo(PolicyOperationStatus.Paid, null, null, new DateOnly(2026, 6, 10), Guid.NewGuid(), new Clock(Now));
        paidBeforeSending.Should().Throw<DomainException>("a request is sent to the insurer before anything is paid");
    }

    [Theory]
    [InlineData(false, 100_000, false)]
    [InlineData(false, 90_000, true)]
    [InlineData(true, 90_000, false)]
    public void No_commission_is_deducted_from_a_remittance_without_the_insurers_agreement(bool netting, int amount, bool refused)
    {
        var clock = new Clock(Now);
        var statement = InsurerStatement.Create(Guid.NewGuid(), Guid.NewGuid(), "2026-05", "XOF", clock);
        statement.ApplyTotals(0, 0, 0, premiumCollected: 100_000m, premiumReversed: 0m, commissionAmount: 10_000m, clock);
        statement.Finalise(Guid.NewGuid(), clock);

        var act = () => statement.RecordRemittance(new DateOnly(2026, 6, 20), "VIR-1", amount, netting, clock);

        if (refused) act.Should().Throw<DomainException>();
        else act.Should().NotThrow();
    }
}
