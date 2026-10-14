namespace Sankore.Modules.Notifications.Features.SmsWebhooks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Proving that a delivery callback really came from the provider.
///
/// <para>
/// <b>The two providers need two different answers, and that asymmetry is theirs, not a design
/// choice here.</b> Twilio signs every request with the account's Auth Token — a real signature
/// over the URL and the form body — so that is what is verified. Brevo does <b>not</b> sign its
/// transactional SMS events at all; its documented protection is IP allow-listing, which a
/// multi-tenant platform behind a load balancer cannot rely on. So Brevo's callback carries a
/// per-tenant token in its URL, derived from the platform signing key, and the endpoint recomputes
/// it.
/// </para>
///
/// <para>
/// <b>Why the Brevo token is DERIVED rather than stored.</b> A stored per-tenant webhook secret
/// would be a new column, a new vault entry, a rotation story and a screen to show it — for a
/// value that only ever needs to be unforgeable and reproducible. HKDF over the key the platform
/// already has gives exactly that, and the same argument <c>KycRemoteSessionOptions</c> makes for
/// the public KYC session key: requiring a new secret would fail every existing deployment on the
/// upgrade that merely adds the feature.
/// </para>
///
/// <para>
/// It is NOT a bearer credential for anything but "post a delivery status for this tenant". The
/// worst a leaked token buys is a forged delivery log row, which is why one derivation is enough
/// and there is no revocation.
/// </para>
/// </summary>
internal sealed class SmsWebhookSignatures(IConfiguration config)
{
    /// <summary>
    /// Versioned, so a future rotation can derive a v2 token and accept both for a window. The
    /// string is part of the derivation: changing it changes every tenant's URL.
    /// </summary>
    private const string DerivationInfo = "sankore:sms-webhook:v1";

    /// <summary>
    /// The Brevo callback token for a tenant. Hex, 32 characters — short enough to paste into a
    /// provider's URL field, long enough that guessing it is not a strategy.
    /// </summary>
    internal string BrevoToken(Guid tenantId)
    {
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(PlatformKey()),
            outputLength: 16,
            info: Encoding.UTF8.GetBytes($"{DerivationInfo}:{tenantId:N}"));

        return Convert.ToHexStringLower(key);
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is this tenant's token. Fixed-time, because the
    /// endpoint answers before doing anything else and a length-prefix comparison would let a
    /// caller recover the token one character at a time.
    /// </summary>
    internal bool IsValidBrevoToken(Guid tenantId, string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented)) return false;

        var expected = Encoding.UTF8.GetBytes(BrevoToken(tenantId));
        var actual = Encoding.UTF8.GetBytes(presented.Trim());

        return expected.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>
    /// Twilio's own scheme: HMAC-SHA1 over the full request URL followed by every POST parameter
    /// in alphabetical order, as <c>name + value</c> with no separators, keyed on the account's
    /// Auth Token, base64-encoded.
    ///
    /// <para>
    /// <b>SHA-1 is Twilio's choice, not this platform's.</b> It is what their signature is defined
    /// as; verifying with anything else verifies nothing. It is used here for authenticity on a
    /// low-value message and not for anything that needs collision resistance.
    /// </para>
    ///
    /// <para>
    /// <b>The URL must be the one TWILIO built, which behind a reverse proxy is not the one the
    /// application sees.</b> TLS terminates upstream, so <c>Request.Scheme</c> is http and
    /// <c>Request.Host</c> may be an internal name — and a signature computed over that never
    /// matches. The caller passes the externally visible URL, reconstructed from the forwarded
    /// headers; this is the single most likely reason a correct integration reports every
    /// callback as forged.
    /// </para>
    /// </summary>
    internal static bool IsValidTwilioSignature(
        string url, IEnumerable<KeyValuePair<string, string>> form, string authToken, string? presented)
    {
        if (string.IsNullOrWhiteSpace(presented) || string.IsNullOrWhiteSpace(authToken))
            return false;

        var builder = new StringBuilder(url);

        // Ordinal, not culture-aware: Twilio sorts bytes, and a culture-sensitive sort disagrees
        // with it on exactly the parameter names that mix case.
        foreach (var pair in form.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append(pair.Value);
        }

        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(authToken));

        var expected = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(presented.Trim());

        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    /// <summary>
    /// The platform signing key, the same one the JWTs are issued under.
    ///
    /// <para>
    /// Reused rather than requiring its own secret, for the reason the class remarks give. It is
    /// never emitted — only derivations of it are — and whoever holds it can already mint an
    /// administrator's token, so it is not the weak link in a forged delivery log.
    /// </para>
    /// </summary>
    private string PlatformKey()
        => config["Jwt:SigningKey"]
           ?? throw new InvalidOperationException(
               "Jwt:SigningKey is required to derive the SMS webhook tokens.");
}
