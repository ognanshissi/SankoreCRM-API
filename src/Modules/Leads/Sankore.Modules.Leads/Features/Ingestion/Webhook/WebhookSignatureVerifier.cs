namespace Sankore.Modules.Leads.Features.Ingestion.Webhook;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Verifies X-Sankore-Signature: t={timestamp},v1={hmac} headers (US-F13.37-BE-17).
/// Supports dual-secret verification (current + previous) for zero-downtime rotation.
/// Timing-safe comparison prevents side-channel attacks.
/// </summary>
internal static class WebhookSignatureVerifier
{
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Parses and verifies a webhook signature header.
    /// Returns null on success, or an error string on failure.
    /// </summary>
    public static string? Verify(
        string? signatureHeader,
        string body,
        string algorithm,
        string currentSecret,
        string? previousSecret,
        DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signatureHeader))
            return "SIGNATURE_MISSING";

        // Parse "t={unix},v1={hex}"
        var parts = signatureHeader.Split(',');
        string? timestamp = null;
        string? signature = null;

        foreach (var part in parts)
        {
            var kv = part.Trim();
            if (kv.StartsWith("t=", StringComparison.Ordinal))
                timestamp = kv[2..];
            else if (kv.StartsWith("v1=", StringComparison.Ordinal))
                signature = kv[3..];
        }

        if (timestamp is null || signature is null)
            return "SIGNATURE_MALFORMED";

        // Validate timestamp within tolerance
        if (!long.TryParse(timestamp, out var unixSeconds))
            return "SIGNATURE_MALFORMED";

        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (Math.Abs((now - signedAt).TotalMinutes) > TimestampTolerance.TotalMinutes)
            return "SIGNATURE_EXPIRED";

        // Build signed payload: "{timestamp}.{body}"
        var signedPayload = $"{timestamp}.{body}";

        // Verify against current secret
        var expectedCurrent = ComputeHmac(algorithm, currentSecret, signedPayload);
        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expectedCurrent)))
            return null; // Valid

        // Verify against previous secret (rotation window)
        if (previousSecret is not null)
        {
            var expectedPrevious = ComputeHmac(algorithm, previousSecret, signedPayload);
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature),
                    Encoding.UTF8.GetBytes(expectedPrevious)))
                return null; // Valid with old secret
        }

        return "SIGNATURE_INVALID";
    }

    internal static string ComputeHmac(string algorithm, string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        byte[] hash = algorithm.ToLowerInvariant() switch
        {
            "sha256" => HMACSHA256.HashData(keyBytes, payloadBytes),
            "sha512" => HMACSHA512.HashData(keyBytes, payloadBytes),
            _ => HMACSHA256.HashData(keyBytes, payloadBytes) // default to sha256
        };

        return Convert.ToHexStringLower(hash);
    }
}
