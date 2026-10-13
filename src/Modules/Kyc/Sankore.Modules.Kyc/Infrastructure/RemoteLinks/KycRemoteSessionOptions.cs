namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The dedicated signing identity of a remote-collection SESSION token (KRM-B-05, §4.3).
///
/// <para>
/// <b>Why a second JWT identity at all.</b> A session token is handed to an anonymous caller who
/// proved only that they hold a link and know a client number and a date of birth. It must be
/// refused everywhere a back-office token is accepted, and a back-office token must be refused on
/// the public session endpoints. Sharing the platform's signing key and audience would make both
/// directions a matter of claim inspection — one forgotten policy away from a public token reading
/// a compliance dashboard.
/// </para>
///
/// <para>
/// <b>Why the key is DERIVED and not a new secret to deploy.</b> Requiring
/// <c>Kyc:RemoteSession:SigningKey</c> would mean every existing deployment fails to start on the
/// upgrade that merely adds this feature, and a secret that must be present before anything boots
/// gets pasted in a hurry. Instead the key is HKDF-SHA256 over the platform's own
/// <c>Jwt:SigningKey</c> with a fixed, versioned info string: genuinely different key material, so
/// a session token's signature cannot validate under the platform scheme, with nothing new to
/// provision. An operator who wants true cryptographic independence sets the explicit key and this
/// class uses it instead.
/// </para>
///
/// <para>
/// Deriving loses nothing that was not already lost: whoever holds <c>Jwt:SigningKey</c> can
/// already mint an administrator's token directly, so there is no threat model in which the derived
/// key is the weak link. What derivation does NOT give is independent rotation — rotating the
/// platform key rotates this one, which invalidates sessions in flight. A customer mid-upload is
/// sent back to the identification screen and their already-uploaded photos are kept, which is the
/// behaviour KRM-F-03 asks for anyway.
/// </para>
/// </summary>
// Public, not internal: the HOST configures the JWT bearer scheme from it, before any
// service provider exists. Everything on it is read-only.
public sealed class KycRemoteSessionOptions
{
    public const string SectionName = "Kyc:RemoteSession";

    /// <summary>
    /// Versioned on purpose. If the derivation ever has to change, bumping this string rotates
    /// every session key without touching the platform secret — and the old sessions stop
    /// validating, which is the correct outcome for a key change.
    /// </summary>
    private const string DerivationInfo = "sankore:kyc:remote-session:v1";

    /// <summary>
    /// Audience of a session token. Nothing else in the platform accepts it, and the platform's
    /// own audience (<c>sankore-crm-api</c>) is not accepted here.
    /// </summary>
    public const string Audience = "sankore-kyc-remote-session";

    public string Issuer { get; private init; } = default!;

    public byte[] SigningKey { get; private init; } = default!;

    /// <summary>
    /// True when an operator provided an explicit key rather than letting it be derived. Surfaced
    /// only so the host can say which it is in a start-up log — a deployment that thinks it has
    /// independent keys and does not should be able to tell.
    /// </summary>
    public bool KeyIsExplicit { get; private init; }

    /// <summary>
    /// Builds the options from configuration, or throws when the platform JWT key is missing —
    /// which it cannot be, since the platform's own bearer scheme is configured from it first.
    /// </summary>
    public static KycRemoteSessionOptions FromConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection(SectionName);
        var explicitKey = section["SigningKey"];

        var platformKey = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(platformKey))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is required: the remote KYC session key is derived from it. "
                + $"Set an explicit {SectionName}:SigningKey to avoid the derivation.");
        }

        var hasExplicitKey = !string.IsNullOrWhiteSpace(explicitKey);

        return new KycRemoteSessionOptions
        {
            // The platform issuer by default: it is the same institution issuing both, and an
            // issuer nobody configured would be a second value to keep in step for no gain.
            Issuer = section["Issuer"] ?? config["Jwt:Issuer"] ?? "sankore",
            SigningKey = hasExplicitKey
                ? Encoding.UTF8.GetBytes(explicitKey!)
                : Derive(platformKey),
            KeyIsExplicit = hasExplicitKey,
        };
    }

    /// <summary>
    /// HKDF-SHA256, 32 bytes. No salt: the input is already a high-entropy secret rather than a
    /// password, and a salt that would have to be stored and kept in step adds a failure mode
    /// without adding strength here.
    /// </summary>
    private static byte[] Derive(string platformKey)
        => HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(platformKey),
            outputLength: 32,
            info: Encoding.UTF8.GetBytes(DerivationInfo));
}
