namespace Sankore.Modules.Integration.Features.Sync;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Verifies an inbound integration webhook (INT-20, criterion 4): HMAC-SHA256 over the raw body,
/// plus an anti-replay window on a timestamp header.
///
/// <para>
/// <b>The signature covers the timestamp, and it has to.</b> The signed payload is
/// <c>"{timestamp}.{rawBody}"</c>. A signature over the body alone would leave the timestamp
/// header free for anyone to rewrite, so a captured request could be replayed for ever simply by
/// putting today's date in the header — the window would reject nothing it was meant to reject and
/// would be pure decoration. Binding the two is what makes the window mean "this request was
/// minted recently", rather than "somebody claims it was".
/// </para>
///
/// <para>
/// <b>What this is not.</b> The window bounds how long a captured request stays usable; it does not
/// make a request single-use. A replay inside the window is still accepted, because refusing one
/// needs a seen-nonce store, which INT-20 does not ask for. It is a deliberately bounded
/// guarantee: the only action a replay can trigger is a targeted re-projection of a customer
/// already known to this connection, which is idempotent.
/// </para>
///
/// <para>
/// <b>One boolean, no reason code.</b> The caller answers 401 with no detail about which check
/// failed, and the cheapest way to keep that promise is to have nothing to leak: a verifier that
/// returned "timestamp expired" versus "bad signature" is an oracle, and the detail reaches a
/// response body the first time somebody adds helpful logging to a handler.
/// </para>
/// </summary>
internal static class SyncWebhookVerifier
{
    /// <summary>Unix seconds at which the sender minted the request.</summary>
    internal const string TimestampHeader = "X-Sankore-Timestamp";

    /// <summary>Lower-case hex HMAC-SHA256 of <c>"{timestamp}.{rawBody}"</c>.</summary>
    internal const string SignatureHeader = "X-Sankore-Signature";

    /// <summary>
    /// The signature a sender holding <paramref name="secret"/> must produce. Shared with the
    /// verification path so there is exactly one definition of the signed payload — the integration
    /// bug this repo has already paid for once is two sides each sure of their own format.
    /// </summary>
    internal static string Sign(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        return Convert.ToHexStringLower(Compute(secret, timestamp, body));
    }

    /// <summary>
    /// Whether this request is authentic and fresh. <c>false</c> for every reason there is.
    /// </summary>
    /// <param name="body">
    /// The <b>raw</b> request bytes. Not a decoded string: a body that is not valid UTF-8, or that
    /// round-trips through a different normalisation, re-encodes to different bytes and the
    /// signature then fails for a reason nobody can diagnose from either end.
    /// </param>
    internal static bool Verify(
        string? secret,
        string? timestamp,
        string? signature,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan replayWindow)
    {
        if (string.IsNullOrWhiteSpace(secret)
            || string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(signature))
            return false;

        if (!long.TryParse(
                timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return false;

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);

        // Absolute difference: a timestamp in the FUTURE is refused as firmly as a stale one.
        // Allowing it would hand an attacker a signature that stays valid for as long as he cares
        // to post-date it, which is the window's whole point undone.
        if ((now - signedAt).Duration() > replayWindow) return false;

        byte[] provided;

        try
        {
            provided = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            // Not hex, or an odd number of digits. Nothing to compare.
            return false;
        }

        var expected = Compute(secret, timestamp, body);

        // Constant time. A byte-by-byte comparison leaks, through its own duration, how many
        // leading bytes were right, which is enough to forge a signature one byte at a time.
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static byte[] Compute(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");

        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload.AsSpan());
        body.CopyTo(payload.AsSpan(prefix.Length));

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload);
    }
}
