namespace Sankore.Modules.Leads.Tests.Features.Ingestion;

using FluentAssertions;
using Sankore.Modules.Leads.Features.Ingestion.Webhook;
using Xunit;

public sealed class WebhookSignatureVerifierTests
{
    private const string Secret = "test-secret-key";
    private const string Body = """{"name":"test"}""";
    private const string Algorithm = "sha256";

    private static string BuildSignature(string secret, string body, long unixSeconds)
    {
        var payload = $"{unixSeconds}.{body}";
        var hmac = WebhookSignatureVerifier.ComputeHmac(Algorithm, secret, payload);
        return $"t={unixSeconds},v1={hmac}";
    }

    [Fact]
    public void Valid_signature_succeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var sig = BuildSignature(Secret, Body, now.ToUnixTimeSeconds());

        var error = WebhookSignatureVerifier.Verify(sig, Body, Algorithm, Secret, null, now);
        error.Should().BeNull();
    }

    [Fact]
    public void Missing_signature_returns_error()
    {
        var error = WebhookSignatureVerifier.Verify(null, Body, Algorithm, Secret, null, DateTimeOffset.UtcNow);
        error.Should().Be("SIGNATURE_MISSING");
    }

    [Fact]
    public void Malformed_signature_returns_error()
    {
        var error = WebhookSignatureVerifier.Verify("garbage", Body, Algorithm, Secret, null, DateTimeOffset.UtcNow);
        error.Should().Be("SIGNATURE_MALFORMED");
    }

    [Fact]
    public void Expired_timestamp_returns_error()
    {
        var now = DateTimeOffset.UtcNow;
        var oldTime = now.AddMinutes(-10);
        var sig = BuildSignature(Secret, Body, oldTime.ToUnixTimeSeconds());

        var error = WebhookSignatureVerifier.Verify(sig, Body, Algorithm, Secret, null, now);
        error.Should().Be("SIGNATURE_EXPIRED");
    }

    [Fact]
    public void Wrong_secret_returns_invalid()
    {
        var now = DateTimeOffset.UtcNow;
        var sig = BuildSignature("wrong-secret", Body, now.ToUnixTimeSeconds());

        var error = WebhookSignatureVerifier.Verify(sig, Body, Algorithm, Secret, null, now);
        error.Should().Be("SIGNATURE_INVALID");
    }

    [Fact]
    public void Previous_secret_accepted_during_rotation()
    {
        var now = DateTimeOffset.UtcNow;
        var oldSecret = "old-secret";
        var sig = BuildSignature(oldSecret, Body, now.ToUnixTimeSeconds());

        var error = WebhookSignatureVerifier.Verify(
            sig, Body, Algorithm, "new-current-secret", oldSecret, now);
        error.Should().BeNull();
    }

    [Fact]
    public void Timing_safe_comparison_rejects_tampered_body()
    {
        var now = DateTimeOffset.UtcNow;
        var sig = BuildSignature(Secret, Body, now.ToUnixTimeSeconds());

        var error = WebhookSignatureVerifier.Verify(
            sig, """{"name":"tampered"}""", Algorithm, Secret, null, now);
        error.Should().Be("SIGNATURE_INVALID");
    }

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha512")]
    public void ComputeHmac_produces_hex_output(string algo)
    {
        var hmac = WebhookSignatureVerifier.ComputeHmac(algo, "key", "payload");
        hmac.Should().MatchRegex("^[0-9a-f]+$");
        hmac.Length.Should().BeGreaterThan(0);
    }
}
