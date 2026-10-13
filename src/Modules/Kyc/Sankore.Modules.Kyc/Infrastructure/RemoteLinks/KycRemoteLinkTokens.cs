namespace Sankore.Modules.Kyc.Infrastructure.RemoteLinks;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Mints and hashes the token that travels in a collection link (KRM-B-03).
///
/// <para>
/// A service and not two static helpers, for one reason: this is the only place in the module that
/// produces a bearer credential, and having it be injectable is what lets a test assert the hash
/// of a known token instead of reaching for reflection.
/// </para>
/// </summary>
public interface IKycRemoteLinkTokens
{
    /// <summary>
    /// A fresh token and its digest. The clear value is returned ONCE and never persisted — the
    /// caller puts it in the URL and lets it go.
    /// </summary>
    (string Token, byte[] Hash) Mint();

    /// <summary>
    /// The digest of a token a public endpoint received, for the index probe. Returns null when the
    /// input cannot be a token of ours — see the implementation for why that check is worth having.
    /// </summary>
    byte[]? HashOrNull(string? token);

    /// <summary>
    /// The URL a customer is sent, with the token in the FRAGMENT.
    ///
    /// <para>
    /// The fragment and not the query string, and that is a security property rather than a style
    /// choice: a fragment is never sent to the server, so the token cannot appear in an nginx
    /// access log, in an upstream proxy's log, or in the <c>Referer</c> of whatever the page loads
    /// next. The Angular page reads it from <c>location.hash</c> and wipes it with
    /// <c>history.replaceState</c>.
    /// </para>
    /// </summary>
    string BuildLink(string fqdn, string token);

    /// <summary>
    /// The back-office address of a KYC file, for a message to an employee (KRM-B-09). No token:
    /// the recipient's own session is the credential.
    /// </summary>
    string BuildBackOfficeLink(string fqdn, Guid kycFileId);
}

/// <inheritdoc />
internal sealed class KycRemoteLinkTokens : IKycRemoteLinkTokens
{
    /// <summary>
    /// 32 bytes — 256 bits of entropy, base64url-encoded to 43 characters. The link is the only
    /// thing standing between the internet and one customer's identification page, so the token
    /// has to be unguessable with no rate limit at all; the rate limits exist for the attempts
    /// that follow, not for finding a link.
    /// </summary>
    private const int TokenBytes = 32;

    /// <summary>Base64url of 32 bytes, unpadded.</summary>
    private const int EncodedLength = 43;

    public (string Token, byte[] Hash) Mint()
    {
        var raw = RandomNumberGenerator.GetBytes(TokenBytes);

        // Base64Url, so the token survives being pasted, clicked and copied out of an email client
        // without any escaping — '+' and '/' in a URL fragment are a support call waiting to
        // happen.
        var token = Base64Url.EncodeToString(raw);

        return (token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public byte[]? HashOrNull(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var trimmed = token.Trim();

        // A length and alphabet check BEFORE the hash. Not for security — hashing is cheap and the
        // index probe would simply miss — but so that a public endpoint can answer "this is not a
        // link" without a database round trip at all. That is what keeps a flood of junk tokens
        // from turning into a flood of queries.
        if (trimmed.Length != EncodedLength)
            return null;

        foreach (var c in trimmed)
        {
            var ok = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return null;
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }

    public string BuildLink(string fqdn, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // A tenant FQDN is stored bare ("imf.sankore.app"), but a value that already carries a
        // scheme must not produce "https://https://…". HTTPS is not negotiable: a browser refuses
        // getUserMedia on an insecure origin, so a link on http would open a page whose camera
        // never starts and whose failure says nothing about why.
        return $"https://{NormalizeHost(fqdn)}/kyc/verification#t={token}";
    }

    /// <summary>
    /// The BACK-OFFICE address of a KYC file, for a message sent to an employee (KRM-B-09).
    ///
    /// <para>
    /// It lives beside <see cref="BuildLink"/> because the host normalisation is the awkward part
    /// and it is the same: a tenant FQDN is stored bare but may carry a scheme, and "https://https://…"
    /// is the bug that gets written twice. HTTPS for the same non-negotiable reason — a session
    /// cookie must not travel in clear — plus the one specific to this product: the back office
    /// refuses to run on an insecure origin.
    /// </para>
    ///
    /// <para>
    /// No token and no fragment. This URL is useless to anybody who is not signed in, which is
    /// what makes it safe to put in an email: the recipient's own session is the credential.
    /// </para>
    /// </summary>
    public string BuildBackOfficeLink(string fqdn, Guid kycFileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fqdn);

        return $"https://{NormalizeHost(fqdn)}/kyc/{kycFileId}";
    }

    private static string NormalizeHost(string fqdn)
    {
        var host = fqdn.Trim().TrimEnd('/');

        if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            host = host["https://".Length..];
        else if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            host = host["http://".Length..];

        return host;
    }
}
