namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Mints and hashes the six-digit code of KRM-B-12.
///
/// <para>
/// Beside <see cref="KycRemoteLinkTokens"/> and for the same reasons: the clear value exists inside
/// one method and only its digest is persisted, so a database dump, a replica and a backup never
/// contain a usable second factor.
/// </para>
///
/// <para>
/// <b>Why the hash is salted with the invitation id.</b> A six-digit code has a million values —
/// small enough that an unsalted SHA-256 of every one of them is a table somebody builds once. The
/// invitation id makes each link's digest unique, so a stolen column is a million hashes per row
/// rather than a lookup. It is not a password hash and does not pretend to be: the real defence is
/// the ten-minute window and the three attempts, which is what KRM-B-12 specifies and what the
/// invitation enforces.
/// </para>
/// </summary>
internal static class KycRemoteOtpCodes
{
    /// <summary>Six digits, as KRM-B-12 asks. Short enough to read over a phone line.</summary>
    internal const int Digits = 6;

    /// <summary>KRM-B-12's window.</summary>
    internal static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);

    /// <summary>KRM-B-12's ceiling. The fourth attempt is refused whatever is typed.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// A fresh code. Uniform over 000000-999999, including the leading-zero values a
    /// <c>Random.Next</c> on a formatted integer would quietly under-represent.
    /// </summary>
    internal static string Mint()
        => RandomNumberGenerator.GetInt32(0, 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The digest stored on the invitation, or null when the input cannot be a code at all.
    ///
    /// <para>
    /// Shape-checked before hashing — exactly six characters, all digits — so a public endpoint
    /// cannot be made to hash an arbitrary string, and so that a blank or malformed entry costs no
    /// attempt-counter increment it did not earn. Whitespace is trimmed: a customer copying a code
    /// out of a text message drags one.
    /// </para>
    /// </summary>
    internal static byte[]? HashOrNull(Guid invitationId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        var trimmed = code.Trim();

        if (trimmed.Length != Digits) return null;
        if (!trimmed.All(char.IsAsciiDigit)) return null;

        // The invitation id is the salt — see the class remarks.
        return SHA256.HashData(
            Encoding.UTF8.GetBytes($"kyc-remote-otp:v1:{invitationId:N}:{trimmed}"));
    }
}
