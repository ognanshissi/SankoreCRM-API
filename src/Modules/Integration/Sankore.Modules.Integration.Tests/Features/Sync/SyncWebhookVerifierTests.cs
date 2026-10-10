namespace Sankore.Modules.Integration.Tests.Features.Sync;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Features.Sync;
using Xunit;

/// <summary>
/// INT-20 criterion 4, the cryptographic half: HMAC signature plus anti-replay window.
/// </summary>
public sealed class SyncWebhookVerifierTests
{
    private const string Secret = "a-dev-only-webhook-secret-at-least-32-bytes";
    private const string OtherSecret = "a-different-dev-only-webhook-secret-value!";

    private static readonly DateTimeOffset Now = new(2026, 4, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"entityType":"Customer","externalId":"CBS-0001"}""");

    [Fact]
    public void A_signature_minted_with_the_shared_secret_verifies()
    {
        var timestamp = Unix(Now);

        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(Secret, timestamp, Body),
                Body, Now, Window)
            .Should().BeTrue();
    }

    [Fact]
    public void A_signature_minted_with_another_secret_does_not_verify()
    {
        var timestamp = Unix(Now);

        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(OtherSecret, timestamp, Body),
                Body, Now, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void A_body_altered_after_signing_does_not_verify()
    {
        var timestamp = Unix(Now);
        var signature = SyncWebhookVerifier.Sign(Secret, timestamp, Body);

        var tampered = Encoding.UTF8.GetBytes(
            """{"entityType":"Customer","externalId":"CBS-0002"}""");

        SyncWebhookVerifier.Verify(Secret, timestamp, signature, tampered, Now, Window)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(-60)]
    [InlineData(6)]
    [InlineData(60)]
    public void A_timestamp_outside_the_window_does_not_verify(int minutesFromNow)
    {
        // Both directions. A future timestamp refused as firmly as a stale one: allowing it would
        // hand an attacker a signature that stays valid for as long as he cares to post-date it,
        // which is the window's whole point undone.
        var signedAt = Now.AddMinutes(minutesFromNow);
        var timestamp = Unix(signedAt);

        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(Secret, timestamp, Body),
                Body, Now, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void A_captured_request_stops_verifying_once_the_window_has_passed()
    {
        // The replay this criterion asks to stop: an authentic request, captured on the wire and
        // re-sent later. The same bytes and the same signature, judged against a later clock.
        var timestamp = Unix(Now);
        var signature = SyncWebhookVerifier.Sign(Secret, timestamp, Body);

        SyncWebhookVerifier.Verify(Secret, timestamp, signature, Body, Now, Window)
            .Should().BeTrue();

        SyncWebhookVerifier.Verify(
                Secret, timestamp, signature, Body, Now.AddMinutes(10), Window)
            .Should().BeFalse();
    }

    [Fact]
    public void Refreshing_the_timestamp_of_a_captured_request_does_not_make_it_verify_again()
    {
        // The attack the window alone does NOT stop, and the reason the signature must cover the
        // timestamp: a replayer whose only obstacle is the clock simply writes a fresh timestamp
        // in the header. Here the signature was minted over the old one, so it no longer matches.
        var minted = Unix(Now);
        var signature = SyncWebhookVerifier.Sign(Secret, minted, Body);

        var later = Now.AddMinutes(10);

        SyncWebhookVerifier.Verify(Secret, Unix(later), signature, Body, later, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void A_signature_that_does_not_cover_the_timestamp_is_refused()
    {
        // The property stated in SyncWebhookVerifier's own comment, demonstrated rather than
        // asserted: a signature computed over the body ALONE — the scheme in which the window
        // would be decorative, because the header could be rewritten freely — does not satisfy
        // this verifier.
        var bodyOnly = Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body));

        SyncWebhookVerifier.Verify(Secret, Unix(Now), bodyOnly, Body, Now, Window)
            .Should().BeFalse();

        // And the converse, so the test is not merely rejecting everything: the body-only
        // signature WOULD have been accepted by a verifier that ignored the timestamp, since it is
        // a genuine HMAC of the body under the real secret.
        bodyOnly.Should().Be(
            Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Body)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-number")]
    public void A_missing_or_unreadable_timestamp_does_not_verify(string? timestamp)
    {
        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(Secret, Unix(Now), Body),
                Body, Now, Window)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-hex-at-all")]
    [InlineData("abc")]
    [InlineData("00")]
    public void A_missing_or_malformed_signature_does_not_verify(string? signature)
    {
        SyncWebhookVerifier.Verify(Secret, Unix(Now), signature, Body, Now, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void A_connection_with_no_secret_never_verifies()
    {
        // Fail closed. "No secret configured" must never read as "no signature required".
        var timestamp = Unix(Now);
        var signature = SyncWebhookVerifier.Sign(Secret, timestamp, Body);

        SyncWebhookVerifier.Verify(null, timestamp, signature, Body, Now, Window)
            .Should().BeFalse();

        SyncWebhookVerifier.Verify("   ", timestamp, signature, Body, Now, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void An_empty_body_is_signable_and_verifiable()
    {
        // A notification can legitimately carry nothing. It must still be authenticated — and an
        // empty body that bypassed verification would be an unauthenticated entry point.
        var timestamp = Unix(Now);
        var empty = Array.Empty<byte>();

        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(Secret, timestamp, empty),
                empty, Now, Window)
            .Should().BeTrue();

        SyncWebhookVerifier.Verify(
                Secret, timestamp, SyncWebhookVerifier.Sign(Secret, timestamp, empty),
                Body, Now, Window)
            .Should().BeFalse();
    }

    [Fact]
    public void The_signature_is_lower_case_hex_of_an_hmac_sha256()
    {
        // The format a provider has to implement. Pinned because the two sides agreeing on it is
        // the whole contract, and M02 has already paid for the version of this where they did not.
        var signature = SyncWebhookVerifier.Sign(Secret, Unix(Now), Body);

        signature.Should().HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");

        signature.Should().Be(
            Convert.ToHexStringLower(
                HMACSHA256.HashData(
                    Encoding.UTF8.GetBytes(Secret),
                    Encoding.UTF8.GetBytes($"{Unix(Now)}.").Concat(Body).ToArray())));
    }

    private static string Unix(DateTimeOffset at)
        => at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
