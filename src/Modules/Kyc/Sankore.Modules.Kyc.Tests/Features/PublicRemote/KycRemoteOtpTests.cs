namespace Sankore.Modules.Kyc.Tests.Features.PublicRemote;

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void The_shipped_sender_refuses_and_says_it_is_unavailable()
    {
        // The whole reason `remote-otp` cannot be enabled in this deployment, asserted rather than
        // assumed: a future registration that quietly answered true would turn the switch on for
        // every tenant with no channel behind it.
        var sender = new UnavailableKycOtpSender();

        sender.IsAvailable.Should().BeFalse();
        sender.SendAsync(Tenant, Customer, "123456", CancellationToken.None)
            .GetAwaiter().GetResult().Should().BeFalse();
    }
}
