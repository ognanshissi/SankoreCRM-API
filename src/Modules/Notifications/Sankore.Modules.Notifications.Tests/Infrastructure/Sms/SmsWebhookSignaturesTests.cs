namespace Sankore.Modules.Notifications.Tests.Infrastructure.Sms;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Sankore.Modules.Notifications.Features.SmsWebhooks;
using Xunit;

/// <summary>
/// Proving a delivery callback came from the provider.
///
/// <para>
/// Both endpoints are ANONYMOUS, so these two checks are the only thing standing between a
/// stranger and a forged delivery record. The Twilio case is verified against a signature computed
/// the way Twilio documents it — URL, then parameters in alphabetical order, HMAC-SHA1, base64 —
/// rather than against this implementation's own output, which would prove only that it agrees
/// with itself.
/// </para>
/// </summary>
public sealed class SmsWebhookSignaturesTests
{
    private const string PlatformKey = "a-dev-only-signing-key-at-least-32-bytes-long";

    private static SmsWebhookSignatures Signatures() => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SigningKey"] = PlatformKey,
            })
            .Build());

    // ── Brevo: a derived URL token ──────────────────────────────────────────

    [Fact]
    public void A_tenants_own_token_is_accepted()
    {
        var signatures = Signatures();
        var tenantId = Guid.NewGuid();

        signatures.IsValidBrevoToken(tenantId, signatures.BrevoToken(tenantId))
            .Should().BeTrue();
    }

    [Fact]
    public void Another_tenants_token_is_refused()
    {
        // The property that matters: the token authorises writing a delivery status for ONE
        // tenant. A single platform-wide token would let any customer of the platform post
        // delivery records against any other's messages.
        var signatures = Signatures();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        signatures.IsValidBrevoToken(mine, signatures.BrevoToken(theirs)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("deadbeef")]
    public void Anything_that_is_not_the_token_is_refused(string? presented)
    {
        Signatures().IsValidBrevoToken(Guid.NewGuid(), presented).Should().BeFalse();
    }

    [Fact]
    public void The_token_is_stable_across_instances()
    {
        // It has to be: the administrator pastes the URL into Brevo once and it has to keep
        // working across deployments and across every instance behind the load balancer. A token
        // derived from anything per-process would work in testing and fail in production on the
        // second pod.
        var tenantId = Guid.NewGuid();

        Signatures().BrevoToken(tenantId).Should().Be(Signatures().BrevoToken(tenantId));
    }

    [Fact]
    public void The_token_is_not_the_platform_key_and_does_not_contain_it()
    {
        // It goes in a URL that is pasted into a third party's configuration screen and appears in
        // their logs. Deriving it is what makes that acceptable.
        var token = Signatures().BrevoToken(Guid.NewGuid());

        token.Should().NotContain(PlatformKey);
        token.Should().HaveLength(32);
        token.Should().MatchRegex("^[0-9a-f]{32}$");
    }

    // ── Twilio: its own signature ───────────────────────────────────────────

    [Fact]
    public void A_signature_computed_the_way_TWILIO_computes_it_is_accepted()
    {
        const string authToken = "twilio-auth-token";
        const string url = "https://imf.sankore.app/api/v1/public/sms-webhooks/twilio";

        var form = new List<KeyValuePair<string, string>>
        {
            new("MessageStatus", "delivered"),
            new("AccountSid", "AC" + new string('0', 32)),
            new("MessageSid", "SM" + new string('1', 32)),
        };

        // Twilio's documented algorithm, written out here rather than reusing the implementation:
        // a test that signs with the code under test proves only that it agrees with itself.
        var signed = url + string.Concat(
            form.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + p.Value));

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed)));

        SmsWebhookSignatures.IsValidTwilioSignature(url, form, authToken, expected)
            .Should().BeTrue();
    }

    [Fact]
    public void The_parameters_are_sorted_so_the_order_they_arrive_in_does_not_matter()
    {
        // A form's order is whatever the HTTP parser produced. Twilio sorts; if this did not, a
        // valid callback would verify or not depending on the order of the fields, which is the
        // kind of intermittent failure nobody can reproduce.
        const string authToken = "t";
        const string url = "https://x/y";

        var a = new List<KeyValuePair<string, string>>
        {
            new("Alpha", "1"), new("Beta", "2"), new("Gamma", "3"),
        };

        var b = new List<KeyValuePair<string, string>>
        {
            new("Gamma", "3"), new("Alpha", "1"), new("Beta", "2"),
        };

        var signed = url + "Alpha1Beta2Gamma3";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed)));

        SmsWebhookSignatures.IsValidTwilioSignature(url, a, authToken, signature)
            .Should().BeTrue();

        SmsWebhookSignatures.IsValidTwilioSignature(url, b, authToken, signature)
            .Should().BeTrue();
    }

    [Fact]
    public void A_signature_over_a_DIFFERENT_url_is_refused()
    {
        // The case that catches a reverse-proxy mistake rather than an attack, and the reason
        // ExternalUrl exists: a signature computed over the internal http:// URL the application
        // sees never matches the external https:// one Twilio signed.
        const string authToken = "t";

        var form = new List<KeyValuePair<string, string>> { new("A", "1") };

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));
        var signature = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes("https://real/hookA1")));

        SmsWebhookSignatures.IsValidTwilioSignature("http://internal/hook", form, authToken, signature)
            .Should().BeFalse();
    }

    [Fact]
    public void A_signature_from_ANOTHER_accounts_token_is_refused()
    {
        const string url = "https://x/y";
        var form = new List<KeyValuePair<string, string>> { new("A", "1") };

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("someone-elses-token"));
        var signature = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(url + "A1")));

        SmsWebhookSignatures.IsValidTwilioSignature(url, form, "our-token", signature)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "token")]
    [InlineData("", "token")]
    [InlineData("sig", null)]
    [InlineData("sig", "")]
    public void A_missing_signature_or_a_missing_token_is_refused(string? signature, string? token)
    {
        // No auth token in the vault means the tenant never configured Twilio — and a callback
        // that arrives anyway must be refused rather than waved through on an empty comparison.
        SmsWebhookSignatures.IsValidTwilioSignature(
                "https://x/y",
                [new KeyValuePair<string, string>("A", "1")],
                token ?? string.Empty,
                signature)
            .Should().BeFalse();
    }
}
