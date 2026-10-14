namespace Sankore.Modules.Kyc.Tests.Features.PublicRemote;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Xunit;

/// <summary>
/// The SMS second factor (KRM-B-12): the code, its window, its three tries, and the things that
/// must not be true of it.
///
/// <para>
/// Domain-level on purpose. The mechanism is the part of this story that exists — the DELIVERY does
/// not, because this platform has no SMS channel (see <see cref="IKycOtpSender"/>) — so what can be
/// proved is exactly what is proved here: that the code is one-time, bounded, expiring and never
/// stored in clear.
/// </para>
/// </summary>
public sealed class KycRemoteOtpTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid File = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();

    private static KycRemoteInvitation Identified(TimeProvider clock)
    {
        var invitation = KycRemoteInvitation.Issue(
            Tenant, File, Customer, validityHours: 72, createdBy: Guid.Empty, clock: clock);

        invitation.AttachTokenAndMarkSent(new byte[32], clock);
        invitation.MarkIdentified(clock);
        return invitation;
    }

    private static byte[] Hash(KycRemoteInvitation invitation, string code)
        => KycRemoteOtpCodes.HashOrNull(invitation.Id, code)!;

    // ── The code itself ─────────────────────────────────────────────────────

    [Fact]
    public void A_minted_code_is_always_six_digits()
    {
        // Including the leading-zero values a Random.Next on a formatted integer under-represents:
        // 1000 draws of a uniform over a million will hit the 0-99999 band about ten times, and
        // every one of them has to be six characters.
        for (var i = 0; i < 1000; i++)
        {
            var code = KycRemoteOtpCodes.Mint();

            code.Should().HaveLength(6);
            code.Should().MatchRegex("^[0-9]{6}$");
        }
    }

    [Fact]
    public void Minting_is_not_a_constant()
    {
        // A weak guard, and worth having: a refactor that returned a fixed string would pass every
        // other case in this file.
        var codes = Enumerable.Range(0, 200).Select(_ => KycRemoteOtpCodes.Mint()).ToHashSet();

        codes.Should().HaveCountGreaterThan(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    [InlineData("12 456")]
    public void Anything_that_is_not_six_digits_hashes_to_nothing(string? candidate)
    {
        // Shape-checked BEFORE hashing, so a public endpoint cannot be made to hash an arbitrary
        // string — and so a malformed entry costs no attempt-counter increment it did not earn.
        KycRemoteOtpCodes.HashOrNull(Guid.NewGuid(), candidate).Should().BeNull();
    }

    [Fact]
    public void Whitespace_around_a_code_is_forgiven()
    {
        var id = Guid.NewGuid();

        KycRemoteOtpCodes.HashOrNull(id, "  123456 ")
            .Should().Equal(KycRemoteOtpCodes.HashOrNull(id, "123456"));
    }

    [Fact]
    public void The_SAME_code_hashes_differently_on_two_links()
    {
        // The invitation id is the salt. Without it, a six-digit code has a million values and an
        // unsalted SHA-256 of every one of them is a table somebody builds once — a stolen column
        // would then be a lookup rather than a million hashes per row.
        var a = KycRemoteOtpCodes.HashOrNull(Guid.NewGuid(), "123456");
        var b = KycRemoteOtpCodes.HashOrNull(Guid.NewGuid(), "123456");

        a.Should().NotEqual(b);
    }

    // ── The invitation's side ───────────────────────────────────────────────

    [Fact]
    public void The_right_code_is_accepted_once_and_never_twice()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);
        var code = KycRemoteOtpCodes.Mint();

        invitation.IssueOtp(Hash(invitation, code), KycRemoteOtpCodes.Validity, clock)
            .IsSuccess.Should().BeTrue();

        invitation.VerifyOtp(Hash(invitation, code), KycRemoteOtpCodes.MaxAttempts, clock)
            .Should().Be(KycOtpVerdict.Accepted);

        // One factor, one session. Leaving the digest in place would make the column a replayable
        // credential for the rest of its ten minutes.
        invitation.VerifyOtp(Hash(invitation, code), KycRemoteOtpCodes.MaxAttempts, clock)
            .Should().Be(KycOtpVerdict.NonePending);
    }

    [Fact]
    public void The_clear_code_is_nowhere_on_the_row()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);
        var code = KycRemoteOtpCodes.Mint();

        invitation.IssueOtp(Hash(invitation, code), KycRemoteOtpCodes.Validity, clock);

        // Every readable property of the aggregate, checked for the digits. The property being
        // protected is that a database dump, a replica and a backup contain no usable factor — and
        // a per-property assertion passes just as well with the code sitting in the one that was
        // not checked.
        var readable = typeof(KycRemoteInvitation)
            .GetProperties()
            .Select(p => p.GetValue(invitation)?.ToString())
            .Where(v => v is not null);

        readable.Should().NotContain(code);
    }

    [Fact]
    public void A_wrong_code_costs_an_attempt_and_the_fourth_is_refused_outright()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);
        var code = KycRemoteOtpCodes.Mint();
        var wrong = Hash(invitation, code == "000000" ? "111111" : "000000");

        invitation.IssueOtp(Hash(invitation, code), KycRemoteOtpCodes.Validity, clock);

        for (var i = 0; i < KycRemoteOtpCodes.MaxAttempts; i++)
        {
            invitation.VerifyOtp(wrong, KycRemoteOtpCodes.MaxAttempts, clock)
                .Should().Be(KycOtpVerdict.Wrong);
        }

        invitation.OtpAttempts.Should().Be(KycRemoteOtpCodes.MaxAttempts);

        // And now even the RIGHT code is refused: the ceiling is on the code, not on the guesser.
        invitation.VerifyOtp(Hash(invitation, code), KycRemoteOtpCodes.MaxAttempts, clock)
            .Should().Be(KycOtpVerdict.TooManyAttempts);
    }

    [Fact]
    public void A_malformed_entry_costs_no_attempt()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);

        invitation.IssueOtp(Hash(invitation, "123456"), KycRemoteOtpCodes.Validity, clock);

        // Null is what HashOrNull answers for a blank or short entry. Charging it would let a
        // script with no code at all burn somebody else's three tries.
        invitation.VerifyOtp(null, KycRemoteOtpCodes.MaxAttempts, clock)
            .Should().Be(KycOtpVerdict.Wrong);

        invitation.OtpAttempts.Should().Be(0);
    }

    [Fact]
    public void A_new_code_resets_the_three_tries_and_NOT_the_link_counter()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);
        var wrong = Hash(invitation, "000000");

        // Two failed identifications AFTER the successful one, so the link counter is non-zero
        // when the codes are issued. Ordered this way deliberately: MarkIdentified resets that
        // counter, so failing before it would leave this test asserting 0 == 0 and proving
        // nothing about what issuing a code does.
        invitation.RecordFailedIdentification(maxAttempts: 5, clock);
        invitation.RecordFailedIdentification(maxAttempts: 5, clock);
        invitation.FailedAttempts.Should().Be(2);

        invitation.IssueOtp(Hash(invitation, "123456"), KycRemoteOtpCodes.Validity, clock);
        invitation.VerifyOtp(wrong, KycRemoteOtpCodes.MaxAttempts, clock);
        invitation.OtpAttempts.Should().Be(1);

        invitation.IssueOtp(Hash(invitation, "654321"), KycRemoteOtpCodes.Validity, clock);

        // The code's count restarts — "send me another one" has to work, because an SMS that does
        // not arrive is the ordinary failure here.
        invitation.OtpAttempts.Should().Be(0);

        // And the LINK counter is untouched by either issue. A new code must never buy an attacker
        // fresh tries at the client number and the date of birth — those two are what the
        // five-attempt lock bounds, and the two counters exist separately for exactly this.
        invitation.FailedAttempts.Should().Be(2);
    }

    [Fact]
    public void A_code_cannot_be_issued_on_a_revoked_link()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);

        invitation.Revoke("Email du client modifié", null, clock);

        // A credential for a journey nobody can continue. The endpoint resolves the link through
        // KycRemoteGate first, so this is the aggregate refusing to be the second place that rule
        // could be forgotten.
        var issued = invitation.IssueOtp(
            Hash(invitation, "123456"), KycRemoteOtpCodes.Validity, clock);

        issued.IsFailure.Should().BeTrue();
        issued.Error.Should().Be(KycErrors.RemoteInvitationNotUsable);
    }

    [Fact]
    public void Verifying_before_any_code_was_issued_reports_NonePending()
    {
        var clock = TimeProvider.System;
        var invitation = Identified(clock);

        invitation.VerifyOtp(Hash(invitation, "123456"), KycRemoteOtpCodes.MaxAttempts, clock)
            .Should().Be(KycOtpVerdict.NonePending);
    }

    // ── Delivery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_with_no_SMS_channel_cannot_offer_a_second_factor()
    {
        // The guard that keeps `remote-otp` from locking a tenant's customers out. Asserted rather
        // than assumed: a change that answered true here would turn the switch on for every tenant
        // with no provider behind it, and every one of their customers would reach a code screen
        // for a message nobody sends.
        var administration = Substitute.For<IAdministrationModule>();

        administration.GetSmsConfigAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns((TenantSmsConfigDto?)null);

        var sender = new SmsKycOtpSender(
            administration, Substitute.For<ICustomersModule>(),
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.IsAvailableAsync(Tenant, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_half_configured_channel_is_not_available_either()
    {
        // CanSend is M12's own computation over four conditions — provider, sender, credential,
        // calling code. This pins that the KYC side TRUSTS it rather than re-deriving it: a
        // second derivation here is a second chance to get one of the four wrong, and wrong in
        // the permissive direction locks a tenant out.
        var administration = Substitute.For<IAdministrationModule>();

        administration.GetSmsConfigAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto(
                ProviderType: "Brevo", SenderId: "SANKORE", AccountSid: null,
                DefaultCallingCode: "225", HasCredential: false,
                CanSend: false, MonthlyQuotaLimit: null, CurrentMonthUsageCount: 0));

        var sender = new SmsKycOtpSender(
            administration, Substitute.For<ICustomersModule>(),
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.IsAvailableAsync(Tenant, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task An_outage_reading_the_channel_fails_CLOSED()
    {
        // An administrator told "unavailable" retries; one allowed to enable a second factor on a
        // channel nobody could verify locks a tenant's customers out. The direction matters more
        // than the availability.
        var administration = Substitute.For<IAdministrationModule>();

        administration.GetSmsConfigAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns<TenantSmsConfigDto?>(_ => throw new InvalidOperationException("M12 is down"));

        var sender = new SmsKycOtpSender(
            administration, Substitute.For<ICustomersModule>(),
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.IsAvailableAsync(Tenant, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_configured_channel_is_available()
    {
        var administration = Substitute.For<IAdministrationModule>();

        administration.GetSmsConfigAsync(Tenant, Arg.Any<CancellationToken>())
            .Returns(new TenantSmsConfigDto(
                ProviderType: "Twilio", SenderId: "+22520123456",
                AccountSid: new string('a', 34), DefaultCallingCode: "225",
                HasCredential: true, CanSend: true,
                MonthlyQuotaLimit: 500, CurrentMonthUsageCount: 12));

        var sender = new SmsKycOtpSender(
            administration, Substitute.For<ICustomersModule>(),
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.IsAvailableAsync(Tenant, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task The_message_carries_the_code_and_the_window_and_NOTHING_else()
    {
        // §8's rule, on the channel where it bites hardest: a text message is readable on a
        // locked screen, so naming the institution and the customer beside a code is how a stolen
        // handset becomes an enrolment. M01 adds client_first_name and company_name itself for
        // the templates that want them; this payload does not ask for either.
        NotifyClientBySmsRequest? sent = null;

        var customers = Substitute.For<ICustomersModule>();
        customers.NotifyClientBySmsAsync(
                Arg.Do<NotifyClientBySmsRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(true);

        var sender = new SmsKycOtpSender(
            Substitute.For<IAdministrationModule>(), customers,
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.SendAsync(Tenant, Customer, "123456", CancellationToken.None))
            .Should().BeTrue();

        sent.Should().NotBeNull();
        sent!.TemplateKey.Should().Be("kyc.remote-otp");
        sent.ClientId.Should().Be(Customer);

        sent.TemplateData.Should().NotBeNull();
        sent.TemplateData!.Keys.Should().BeEquivalentTo(["code", "minutes"]);
        sent.TemplateData["code"].Should().Be("123456");
        sent.TemplateData["minutes"].Should().Be((int)KycRemoteOtpCodes.Validity.TotalMinutes);
    }

    [Fact]
    public async Task Two_sends_of_the_SAME_code_window_share_an_idempotency_key()
    {
        // What stops a retried request from charging the tenant twice. Keyed on nothing it would
        // double-bill; keyed on the invitation alone a customer asking for a SECOND code would be
        // silently given none, because M08 drops a duplicate key.
        var keys = new List<string>();

        var customers = Substitute.For<ICustomersModule>();
        customers.NotifyClientBySmsAsync(
                Arg.Do<NotifyClientBySmsRequest>(r => keys.Add(r.IdempotencyKey)),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var sender = new SmsKycOtpSender(
            Substitute.For<IAdministrationModule>(), customers,
            NullLogger<SmsKycOtpSender>.Instance);

        await sender.SendAsync(Tenant, Customer, "123456", CancellationToken.None);
        await sender.SendAsync(Tenant, Customer, "123456", CancellationToken.None);

        keys.Should().HaveCount(2);
        keys[0].Should().Be(keys[1]);

        // And a DIFFERENT customer never shares one, however close in time.
        await sender.SendAsync(Tenant, Guid.NewGuid(), "123456", CancellationToken.None);
        keys[2].Should().NotBe(keys[0]);
    }

    [Fact]
    public async Task A_message_that_could_not_be_queued_is_reported_as_not_sent()
    {
        // The identification endpoint turns this into a 503 and keeps the link usable, rather than
        // opening a session: falling back to one would switch the tenant's second factor off at
        // the exact moment the channel failed.
        var customers = Substitute.For<ICustomersModule>();
        customers.NotifyClientBySmsAsync(
                Arg.Any<NotifyClientBySmsRequest>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sender = new SmsKycOtpSender(
            Substitute.For<IAdministrationModule>(), customers,
            NullLogger<SmsKycOtpSender>.Instance);

        (await sender.SendAsync(Tenant, Customer, "123456", CancellationToken.None))
            .Should().BeFalse();
    }
}
