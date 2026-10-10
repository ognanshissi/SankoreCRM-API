namespace Sankore.Modules.Integration.Features.RelayAgents;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The enrolment token: how one is minted, how it is hashed, and how a presented one is compared
/// (INT-27, criterion 1).
///
/// <para>
/// Both halves of the exchange live here on purpose. The generating side and the verifying side
/// have to agree on the hash function and on its encoding down to the letter case, and they are
/// two different handlers reached by two different HTTP surfaces — one behind a JWT, one
/// anonymous. A second, equivalent-looking <c>SHA256</c> call in the other handler is exactly the
/// kind of drift that reads as "the token is invalid" rather than as a bug, the same trap M02
/// documented for its activation-token purpose chain.
/// </para>
///
/// <para>
/// <b>Hex, not Base64.</b> The token travels in a request body here, but an installer script will
/// inevitably put it on a command line, in a URL or in an environment file, and this repository
/// has already paid for Base64 tokens in URLs: a raw <c>+</c> is read back as a space and the
/// token no longer verifies (see <c>CreateUserHandler</c>'s <c>Uri.EscapeDataString</c>).
/// Lower-case hexadecimal has no character that any transport rewrites, and 32 random bytes carry
/// the same 256 bits of entropy either way.
/// </para>
/// </summary>
internal static class RelayEnrolmentToken
{
    /// <summary>
    /// 32 bytes — 256 bits. The token is a bearer credential with no second factor, so its
    /// strength is its only defence against being guessed; the short lifetime below bounds how
    /// long a leaked one is worth anything, it does not make a weak one safe.
    /// </summary>
    private const int EntropyBytes = 32;

    /// <summary>
    /// How long a freshly minted token is worth presenting.
    ///
    /// <para>
    /// Thirty minutes is an installation window, not a mail-reading window: unlike M12's
    /// activation link — which may sit unread for days and therefore gets its own seven-day
    /// lifespan — this token is read out to an engineer who is already in front of the on-premise
    /// machine. It is deliberately a constant rather than a setting: a configurable lifetime is a
    /// configurable way to make the token long-lived, and nothing in INT-27 asks for one.
    /// </para>
    /// </summary>
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A fresh token. Returned to the caller of the generating endpoint exactly once and never
    /// stored: only <see cref="Hash"/> of it reaches the database.
    /// </summary>
    internal static string Generate()
        => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(EntropyBytes));

    /// <summary>
    /// SHA-256 of the token, lower-case hexadecimal — the encoding
    /// <c>IntegrationRelayAgent.EnrolmentTokenHash</c> documents and the column is sized for (64
    /// characters).
    ///
    /// <para>
    /// A plain hash with no salt and no work factor, which is right here and would be wrong for a
    /// password: the token is 256 bits of uniform randomness, so there is no dictionary to try and
    /// nothing a per-row salt would defend against. What the hash buys is that a stolen database
    /// backup contains no usable agent identity.
    /// </para>
    /// </summary>
    internal static string Hash(string token)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Whether a presented token's hash is the one stored for an agent, compared in constant time.
    ///
    /// <para>
    /// The row is found by an indexed equality on the hash — that is unavoidable, the token is the
    /// only thing the presenter has and the lookup has to use an index — and this comparison is
    /// then the authoritative one. <see cref="CryptographicOperations.FixedTimeEquals"/> rather
    /// than <c>==</c> because a string comparison returns as soon as two characters differ, and
    /// the time it took is a measurement an attacker can make from the outside. Over a hash of a
    /// 256-bit token that measurement is worth very little; using it anyway costs nothing and
    /// means the next person who reuses this helper for something weaker inherits the right
    /// behaviour.
    /// </para>
    ///
    /// <para>
    /// A null stored hash — a token already exchanged, or an agent revoked — is a refusal, not a
    /// match. And the length check is first because <c>FixedTimeEquals</c> returns false
    /// immediately on a length mismatch anyway; keeping it explicit documents that two hashes of
    /// this function are always the same length, so the early exit leaks nothing about the secret.
    /// </para>
    /// </summary>
    internal static bool Matches(string presentedHash, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return false;
        if (presentedHash.Length != storedHash.Length) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presentedHash),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
