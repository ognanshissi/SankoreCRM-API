namespace Sankore.Modules.Integration.Infrastructure.Transport;

using System.Security.Cryptography;

/// <summary>
/// Compares the host key an SFTP server presented against the fingerprint the vault holds
/// (INT-24, criterion 4).
///
/// <para>
/// Its own type, and computed from the raw host key rather than read off an SSH.NET event
/// property, for two reasons. First it is testable without a server: a fingerprint check is the
/// single thing standing between a deposit and a man-in-the-middle, and "we trust whatever
/// answers" is not a failure any integration test would notice. Second the property names SSH.NET
/// exposes for fingerprints have changed across releases, while the host key bytes have not — a
/// check written against the bytes cannot silently stop checking after a package bump.
/// </para>
///
/// <para>
/// Both spellings an operator can obtain are accepted, because an operator who has to re-encode a
/// fingerprint by hand is an operator who eventually pastes the wrong one:
/// </para>
/// <list type="bullet">
/// <item><c>SHA256:47DEQpj8HBSa+/TImW+5JCeuQeR…</c> — what <c>ssh-keyscan host | ssh-keygen -lf -</c>
///   prints; base64, padding optional, prefix optional;</item>
/// <item><c>a1:b2:c3:…</c> or <c>a1b2c3…</c> — hex, colons and case irrelevant.</item>
/// </list>
///
/// <para>
/// MD5 fingerprints are deliberately NOT accepted. The legacy <c>ssh-keygen -E md5</c> form is
/// still what some runbooks carry, and accepting it would let a collision-prone digest authorise
/// the connection that carries a tenant's whole customer file.
/// </para>
/// </summary>
internal static class SftpHostKeyFingerprint
{
    /// <summary>Hex characters for a SHA-256 digest: 32 bytes.</summary>
    private const int Sha256HexLength = 64;

    /// <summary>
    /// <c>true</c> when <paramref name="hostKey"/> hashes to <paramref name="configured"/>.
    ///
    /// <para>
    /// A blank configured value is <b>false</b>, never "accept": that is the whole fail-closed
    /// rule, and expressing it here rather than at the call site means a second call site cannot
    /// forget it.
    /// </para>
    /// </summary>
    internal static bool Matches(byte[]? hostKey, string? configured)
    {
        if (hostKey is null || hostKey.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(configured)) return false;

        var expected = ParseExpected(configured);
        if (expected is null) return false;

        var actual = SHA256.HashData(hostKey);

        // Fixed-time compare. The fingerprint is not a secret, but the comparison is free to be
        // constant-time and this repo already holds that line for its API key.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// The 32 expected bytes, or <c>null</c> when the configured text is not a SHA-256
    /// fingerprint in either accepted spelling. Null is a refusal, not a fallback.
    /// </summary>
    private static byte[]? ParseExpected(string configured)
    {
        var text = configured.Trim();

        if (text.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            text = text["SHA256:".Length..];

        text = text.Trim();

        // Hex first: a hex string of the right length cannot also be a valid 32-byte base64
        // payload, so the order carries no ambiguity.
        var hex = text.Replace(":", string.Empty, StringComparison.Ordinal);
        if (hex.Length == Sha256HexLength && hex.All(char.IsAsciiHexDigit))
            return Convert.FromHexString(hex);

        // Base64, padding optional — ssh-keygen prints it unpadded.
        var base64 = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        var padding = base64.Length % 4;
        if (padding is 2 or 3) base64 += new string('=', 4 - padding);
        else if (padding == 1) return null;

        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == SHA256.HashSizeInBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
