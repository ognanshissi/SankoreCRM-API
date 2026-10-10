namespace Sankore.Modules.Integration.Features.RelayAgents;

using System.Text.RegularExpressions;

/// <summary>
/// The shape of a client-certificate thumbprint, in one place.
///
/// <para>
/// ──────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>THE PKI GAP, STATED PLAINLY.</b> INT-27's criterion 1 says the agent "exchanges the token
/// for a client certificate specific to the tenant". <b>This repository has no certificate
/// authority</b>, and INT-27 cannot settle one: which CA signs an agent's certificate, where the
/// chain is validated (Kestrel, an ingress, a service mesh), how a certificate is rotated before
/// it expires and how a revocation reaches the TLS terminator are infrastructure decisions with a
/// deployment topology behind them. So the exchange <b>records the thumbprint of the certificate
/// the agent itself holds</b> and returns an admission, rather than signing anything.
/// </para>
/// <para>
/// What that does deliver: the agent's identity is pinned to one certificate, chosen at enrolment
/// time by whoever held a valid single-use token, and the platform can refuse every other one.
/// What it does not deliver: proof that the certificate was issued by anybody in particular.
/// Mutual-TLS trust anchoring is therefore an <b>open infrastructure decision</b>, named here
/// rather than faked — a homemade CA in a feature folder would be a worse answer than an honest
/// gap, because it would look like the problem was solved.
/// </para>
/// <para>
/// <b>Part of that same decision: client certificates have to be enabled on the route.</b> Either
/// Kestrel collects them (<c>ClientCertificateMode.AllowCertificate</c> on the HTTPS endpoint) or
/// the reverse proxy terminating TLS forwards the certificate to it. Until that is done,
/// <c>POST /integration/relay-agents/heartbeat</c> sees no certificate and <b>refuses every
/// call</b> — which is the correct failure mode and is stated rather than softened: a relay that
/// cannot report in shows up as a relay that has not reported in, while a route that accepted a
/// body-supplied identifier instead would let anyone make a dead relay look alive.
/// </para>
/// <para>
/// ──────────────────────────────────────────────────────────────────────────────────────────
/// </para>
/// </summary>
internal static partial class RelayCertificateThumbprint
{
    /// <summary>
    /// SHA-256 hexadecimal: 64 characters, either case accepted on the wire. The column is sized
    /// for exactly that, and the aggregate lower-cases what it stores.
    /// </summary>
    internal const string Pattern = "^[0-9a-fA-F]{64}$";

    /// <summary>
    /// SHA-256 and not SHA-1. A SHA-1 thumbprint is 40 characters, so a tool configured to print
    /// one is refused with a message instead of writing a value that would never match anything
    /// the channel later computes — which would read as "the agent cannot connect", with nothing
    /// to point at.
    /// </summary>
    internal const string Requirement =
        "A certificate thumbprint must be the SHA-256 fingerprint as 64 hexadecimal characters.";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    internal static bool IsWellFormed(string? value)
        => !string.IsNullOrWhiteSpace(value) && Shape().IsMatch(value.Trim());

    /// <summary>
    /// Lower-case and trimmed — the form <c>IntegrationRelayAgent.Admit</c> stores, so a lookup
    /// by thumbprint and a stored thumbprint cannot disagree over case. PostgreSQL string
    /// equality is case-SENSITIVE, which is the same trap the repository hit with email template
    /// locales and fixed with <c>LanguageCode</c>.
    /// </summary>
    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
