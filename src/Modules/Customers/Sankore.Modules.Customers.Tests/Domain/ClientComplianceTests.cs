namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Insurance lot 4 on the client: payment instruments and the LBC/FT suspicion alert.</summary>
public sealed class ClientComplianceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_payment_instrument_shows_only_its_last_digits()
    {
        var instrument = ClientPaymentInstrument.Register(Guid.NewGuid(), Guid.NewGuid(), PaymentInstrumentKind.MobileMoney,
            "Orange Money", "07 08 09 18 01", "v1:cipher", "Awa Koné", Guid.NewGuid(), Now);

        instrument.MaskedIdentifier.Should().Be("•••• 1801");
        instrument.EncryptedIdentifier.Should().Be("v1:cipher");
    }

    [Fact]
    public void A_declaration_to_the_CENTIF_carries_its_date_and_reference()
    {
        var alert = SuspicionAlert.Raise(Guid.NewGuid(), Guid.NewGuid(), "v1:facts", Guid.NewGuid(), Now);

        alert.Handle(SuspicionAlertStatus.Declared, null, null, null, Guid.NewGuid(), Now)
            .Error.Should().Be("SUSPICION_ALERT_DECLARATION_REQUIRED");

        alert.Handle(SuspicionAlertStatus.Declared, new DateOnly(2026, 10, 26), "CENTIF-2026-42", null, Guid.NewGuid(), Now)
            .IsSuccess.Should().BeTrue();

        alert.Handle(SuspicionAlertStatus.UnderReview, null, null, null, Guid.NewGuid(), Now)
            .Error.Should().Be("SUSPICION_ALERT_CLOSED", "a declared alert is final");
    }

    [Fact]
    public void Closing_without_declaration_needs_the_officers_reason()
    {
        var alert = SuspicionAlert.Raise(Guid.NewGuid(), Guid.NewGuid(), "v1:facts", Guid.NewGuid(), Now);

        alert.Handle(SuspicionAlertStatus.ClosedWithoutDeclaration, null, null, null, Guid.NewGuid(), Now)
            .Error.Should().Be("SUSPICION_ALERT_REASON_REQUIRED");
    }
}
