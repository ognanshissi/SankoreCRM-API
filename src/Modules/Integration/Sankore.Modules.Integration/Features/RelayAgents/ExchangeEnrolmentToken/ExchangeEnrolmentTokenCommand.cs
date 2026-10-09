namespace Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// The agent presents its single-use enrolment token and pins the certificate it will connect
/// with (INT-27, criterion 1).
///
/// <para>
/// <b>Anonymous but not unauthenticated: the token IS the credential.</b> The presenter has no
/// JWT, no tenant header and no user — that is the point of the exchange, which is what
/// ESTABLISHES which tenant the agent belongs to. Hence the lookup is across tenants, and hence
/// the unique index on the token hash is not tenant-scoped.
/// </para>
///
/// <para>
/// <b>What the enrolment trusts, stated plainly: the token, for its lifetime.</b> A leaked token
/// IS an impersonated agent — whoever holds it can pin a certificate they control and will then be
/// admitted into that tenant's relay for as long as nobody revokes the agent. That is why
/// single-use (the hash is cleared by <c>Admit</c>) and the short expiry are <b>load-bearing and
/// not decorative</b>: they are the entire window in which a leak is exploitable. Anyone tempted
/// later to lengthen that window, or to allow a token to be presented twice "for retries", is
/// trading away the only limit on an undetectable takeover of an on-premise relay.
/// </para>
///
/// <para>
/// <see cref="ICommand"/> is criterion 4 even here. The actor on the audit row is empty and the
/// tenant on it is <c>Guid.Empty</c>, because <c>AuditBehavior</c> reads both from the request
/// context and there is no identity on an anonymous call — the row still records that an exchange
/// was attempted and whether it succeeded, and the handler logs the agent and tenant it resolved.
/// An exchange that left no trace at all would be the one step of an agent's life nobody could
/// reconstruct.
/// </para>
/// </summary>
/// <param name="Token">
/// The clear enrolment token. <see cref="SensitiveDataAttribute"/> is load-bearing:
/// <c>ICommand</c> makes <c>AuditBehavior</c> serialise this record into <c>audit.entries</c>,
/// which is append-only and kept for years, and this value is a bearer credential for as long as
/// it lives. The attribute replaces it with "***" there and keeps the field name.
/// </param>
/// <param name="CertificateThumbprint">
/// SHA-256 fingerprint, 64 lower-case hexadecimal characters, of the certificate the agent
/// presented in the TLS handshake.
/// <para>
/// <b>SERVER-SET ONLY.</b> <see cref="ExchangeEnrolmentTokenEndpoint"/> computes it from
/// <c>HttpContext.Connection</c>; <see cref="ExchangeEnrolmentTokenRequest"/> has no such field.
/// TLS has already proven the caller holds the matching private key, which is proof of possession
/// for free and with no certificate authority involved — we are pinning a certificate, not
/// trusting one.
/// </para>
/// <para>
/// Marked sensitive all the same, though a thumbprint is not a secret: it is the identifier the
/// agent channel authenticates on, so writing it verbatim into an append-only table that many
/// people can read would hand out an impersonation handle for anything that ever trusted a
/// thumbprint alone. Same reason the read side does not return it.
/// </para>
/// </param>
internal sealed record ExchangeEnrolmentTokenCommand(
    [property: SensitiveData] string Token,
    [property: SensitiveData] string CertificateThumbprint)
    : IRequest<Result<RelayAgentAdmissionDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationRelayAgent";

    /// <summary>
    /// Null: which agent this is cannot be known before the token has been resolved, and the
    /// token is the only thing the request carries. Putting the agent id here would mean
    /// accepting one in the request body, which is the exact thing §5bis forbids.
    /// </summary>
    public string? ResourceId => null;
}

/// <summary>
/// What the agent learns once it has been admitted: who it is, and which tenant it serves.
///
/// <para>
/// The tenant id is returned because the agent needs it to label itself and its logs, and because
/// the presenter of a valid single-use token is by definition authorised to know it — the token
/// was minted by an administrator of that tenant. It is an OUTPUT of the exchange and never an
/// input to it.
/// </para>
///
/// <para>
/// No thumbprint in the answer. The agent presented the certificate in the handshake, so it
/// already knows which one was pinned, and a response is one more place a value an attacker can
/// impersonate with would sit — in a log, a proxy cache, an installer's console output.
/// </para>
/// </summary>
internal sealed record RelayAgentAdmissionDto(
    Guid AgentId,
    Guid TenantId,
    string Name,
    DateTimeOffset CertificateIssuedAt);
