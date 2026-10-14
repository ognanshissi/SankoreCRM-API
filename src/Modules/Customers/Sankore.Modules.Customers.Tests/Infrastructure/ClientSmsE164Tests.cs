namespace Sankore.Modules.Customers.Tests.Infrastructure;

using FluentAssertions;
using Sankore.Modules.Customers.Infrastructure.Notifications;
using Xunit;

/// <summary>
/// Turning a stored telephone number into the address a provider will dial.
///
/// <para>
/// <b>Being wrong here sends a one-time code to a stranger.</b> M01 stores numbers as they were
/// dialled — <c>SensitiveValueNormalizer.NormalizePhone</c> deliberately infers no indicatif,
/// because inferring one wrongly makes two different subscribers collide in a blind index — so the
/// conversion to E.164 is a real decision with a real failure mode, and it gets cases of its own
/// rather than being reached only through a seeded client and a mocked provider.
/// </para>
/// </summary>
public sealed class ClientSmsE164Tests
{
    /// <summary>Côte d'Ivoire, where the tenants of this platform mostly are.</summary>
    private const string Civ = "225";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a number")]
    public void Nothing_usable_produces_nothing(string? stored)
    {
        ClientSmsNotifier.ToE164(stored, Civ).Should().BeNull();
    }

    // ── Already international ───────────────────────────────────────────────

    [Theory]
    [InlineData("+2250708091801")]
    [InlineData("+225 07 08 09 18 01")]
    [InlineData("00225 07 08 09 18 01")]
    [InlineData("+225-07-08-09-18-01")]
    public void A_number_that_declares_its_country_keeps_it(string stored)
    {
        // Every spelling an agent might have typed. The leading zero of 0708091801 is PART of the
        // Ivorian number since the 2021 migration to ten digits, so it survives — dropping it
        // would dial a different subscriber.
        ClientSmsNotifier.ToE164(stored, Civ).Should().Be("+2250708091801");
    }

    [Fact]
    public void An_international_number_ignores_the_tenants_calling_code()
    {
        // A customer captured in France, on an Ivorian tenant. Prefixing the tenant's code would
        // produce +22533… and dial somebody in Abidjan.
        ClientSmsNotifier.ToE164("+33612345678", Civ).Should().Be("+33612345678");
    }

    [Fact]
    public void A_trunk_zero_is_dropped_only_for_the_plans_that_have_one()
    {
        // France has a trunk 0, so +33 0 6 … and +33 6 … are the same subscriber. NormalizePhone
        // owns that table; this asserts the conversion inherits it rather than re-deciding.
        ClientSmsNotifier.ToE164("+33 0 6 12 34 56 78", Civ).Should().Be("+33612345678");
    }

    // ── Local form ──────────────────────────────────────────────────────────

    [Fact]
    public void A_local_number_gets_the_tenants_calling_code()
    {
        ClientSmsNotifier.ToE164("07 08 09 18 01", Civ).Should().Be("+2250708091801");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    public void A_local_number_with_NO_calling_code_is_refused(string? callingCode)
    {
        // The whole reason TenantSmsSettings.DefaultCallingCode is required to enable the channel.
        // Guessing here is the one thing this method must not do: an eight-digit number means a
        // different person in Abidjan and in Dakar.
        ClientSmsNotifier.ToE164("0708091801", callingCode).Should().BeNull();
    }

    [Theory]
    [InlineData("+225")]
    [InlineData("225")]
    [InlineData(" 225 ")]
    public void The_calling_code_is_read_as_digits_whatever_was_typed(string callingCode)
    {
        // An administrator pastes "+225" into a field labelled "indicatif". A stray plus in the
        // MIDDLE of an E.164 address is a message every provider rejects, so it is stripped.
        ClientSmsNotifier.ToE164("0708091801", callingCode).Should().Be("+2250708091801");
    }

    [Fact]
    public void A_local_number_of_a_different_tenant_country_follows_that_tenant()
    {
        // Senegal. The code comes from the TENANT's configuration, not from the number.
        ClientSmsNotifier.ToE164("771234567", "221").Should().Be("+221771234567");
    }

    // ── Plausibility ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12")]
    [InlineData("1234")]
    public void Something_too_short_to_be_a_number_is_refused(string stored)
    {
        // A truncated entry or a short code. A provider rejects it, at the cost of a billed
        // attempt and a dead-lettered row — cheaper to refuse here.
        ClientSmsNotifier.ToE164(stored, null).Should().BeNull();
    }

    [Fact]
    public void Something_longer_than_E164_allows_is_refused()
    {
        // E.164 caps at fifteen digits. Sixteen is a typo or two numbers run together.
        ClientSmsNotifier.ToE164("+1234567890123456", Civ).Should().BeNull();
    }

    [Fact]
    public void A_local_number_that_the_calling_code_makes_too_long_is_refused()
    {
        // The combination is checked, not just the parts: a thirteen-digit local entry plus a
        // three-digit code is sixteen, which is not a telephone number however plausible each
        // half looked.
        ClientSmsNotifier.ToE164("1234567890123", Civ).Should().BeNull();
    }

    [Fact]
    public void The_result_always_starts_with_a_plus_and_holds_only_digits_after_it()
    {
        // What every provider's API requires. A value that merely "looks like" a number — spaces,
        // dashes, a leading 00 — is rejected by Twilio with an error about an invalid To.
        var result = ClientSmsNotifier.ToE164("00225 07.08.09.18.01", Civ);

        result.Should().NotBeNull();
        result.Should().MatchRegex(@"^\+[0-9]{8,15}$");
    }
}
