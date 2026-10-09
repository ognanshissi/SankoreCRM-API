namespace Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;

using System.Security.Cryptography;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The agent's half of the enrolment (INT-27, criterion 1). On the PUBLIC surface: the machine
/// calling it has no JWT, no user and no tenant — resolving its tenant is what this call does.
///
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────<br/>
/// <b>THE CERTIFICATE IS TAKEN FROM THE TLS HANDSHAKE, NOT FROM THE BODY</b>, exactly as on the
/// heartbeat. The body carries the token and nothing else.
/// </para>
/// <para>
/// This is not because a body-supplied thumbprint would be forgeable <i>here</i> — at enrolment
/// the <b>token</b> is the credential, and whoever holds a valid token can bind a certificate they
/// genuinely control anyway, so proof of possession defends against nothing the token does not
/// already decide. It is because the handshake gives that proof for free: TLS has already shown
/// the caller holds the private key, with no certificate authority and no chain validation
/// involved. We are <b>pinning</b> a certificate, not trusting one — it need not be signed by
/// anybody we know, it only has to be the one this agent will keep using. Taking a value from the
/// body when the connection already carries a proven one is leaving that for free.
/// </para>
/// <para>
/// ───────────────────────────────────────────────────────────────────────────────────────────
/// </para>
///
/// <para>
/// <c>401</c> on every refusal, with the single code
/// <c>INTEGRATION_RELAY_ENROLMENT_NOT_PENDING</c>: unknown token, expired token, already-exchanged
/// token, revoked agent, no client certificate presented, and a certificate already bound to
/// another agent. 401 and not 404, because the token is a credential and a refused credential is
/// an authentication failure; one code, because those cases told apart would be an enumeration
/// oracle — "already bound" in particular would confirm to an unauthenticated caller that a
/// thumbprint it names is known to the platform. Returning the code in the body is safe for the
/// same reason it is useful: there is only one, so it says nothing the status code does not, and
/// an installer can print a message instead of "something went wrong".
/// </para>
/// </summary>
internal static class ExchangeEnrolmentTokenEndpoint
{
    internal static IEndpointRouteBuilder MapExchangeEnrolmentToken(this IEndpointRouteBuilder app)
    {
        app.MapPost("enrol", Handle)
            // Anonymous in the sense that there is no JWT and no tenant header. The call is still
            // authenticated — by the single-use token in its body.
            .AllowAnonymous()
            // The module's existing public-ingest policy — 60 requests a minute per remote IP.
            // Reused rather than invented: a policy name the host does not declare is not a
            // tighter limit, it is no limit at all, silently (the lesson SyncWebhookEndpoints
            // records). Its partition key includes a `connectionId` route value this route does
            // not have, so the window degrades to one per IP across the module's public surface —
            // which is the right shape here anyway: an agent enrols once in its life.
            .RequireRateLimiting("integration-webhook")
            .WithName("ExchangeIntegrationRelayEnrolmentToken")
            .WithTags("Integration")
            .WithSummary("Exchange a relay agent's enrolment token for an admitted certificate")
            .WithDescription(
                "Called by the on-premise relay agent itself, once, over mutual TLS: the body "
                + "carries the single-use token an administrator generated, and the certificate "
                + "to pin is the one presented in the handshake — the platform computes its "
                + "SHA-256 thumbprint itself and never accepts one in a payload. On success the "
                + "agent is admitted, the token is burned, and the answer carries the agent id "
                + "and the tenant it serves; the tenant is established HERE, from the token, and "
                + "is never accepted in a request body. Every refusal — unknown, expired, already "
                + "exchanged, revoked, no client certificate, or a certificate already bound to "
                + "another agent — answers the same 401 and the same code, so the route cannot be "
                + "used to probe which agents or which certificates exist. Audited.")
            .Produces<RelayAgentAdmissionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        ExchangeEnrolmentTokenRequest req,
        HttpContext http,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(http);

        var thumbprint = await RelayClientCertificate.ThumbprintAsync(http);

        // Fail closed. No certificate means nothing to pin and no proof of a private key, and the
        // refusal is the uniform one: an unauthenticated caller must not learn that mutual TLS is
        // not configured on this deployment, which would be an invitation rather than a
        // diagnostic.
        if (thumbprint is null)
            return Refused();

        var result = await sender.Send(
            new ExchangeEnrolmentTokenCommand(req.Token, thumbprint), ct);

        // One failure branch, because the handler has one failure code. A switch here would be an
        // invitation to add a second.
        return result.IsSuccess ? Results.Ok(result.Value) : Refused();
    }

    private static IResult Refused()
        => Results.Json(
            new { error = IntegrationErrors.RelayEnrolmentNotPending },
            statusCode: StatusCodes.Status401Unauthorized);
}

/// <summary>
/// The token, and nothing else.
///
/// <para>
/// It travels in the BODY, never in the route or the query string: a URL reaches the access log of
/// every proxy on the way and survives in shell history, and the token is a bearer credential.
/// </para>
///
/// <para>
/// No certificate thumbprint: the certificate comes from the handshake, where TLS has proven the
/// caller holds its private key. <c>HeartbeatDoesNotTrustTheBodyTests</c> pins that no request
/// contract of this area ever gains one back.
/// </para>
/// </summary>
internal sealed record ExchangeEnrolmentTokenRequest(string Token);

/// <summary>
/// The thumbprint of the certificate proven in the TLS handshake. Shared by the two public routes
/// of this area, so both compute it the same way — the value has to match, byte for byte and case
/// for case, what <c>IntegrationRelayAgent.Admit</c> stored at enrolment, and two independent
/// copies of this three-line method is exactly how that stops being true.
/// </summary>
internal static class RelayClientCertificate
{
    /// <summary>
    /// Lower-case hexadecimal SHA-256, or <c>null</c> when no client certificate was presented.
    ///
    /// <para>
    /// <c>GetCertHash(HashAlgorithmName.SHA256)</c> and not <c>X509Certificate2.Thumbprint</c>,
    /// which is SHA-1 and 40 characters: the stored value is SHA-256 (64 characters), and a
    /// mismatch of algorithms would read as "no agent is admitted", for every agent, with nothing
    /// to point at.
    /// </para>
    ///
    /// <para>
    /// <c>GetClientCertificateAsync</c> rather than the synchronous <c>ClientCertificate</c>
    /// property: on HTTP/1.1 with renegotiation the certificate may not have been collected when
    /// the request starts, and the property would read null for a caller that did present one.
    /// </para>
    /// </summary>
    internal static async Task<string?> ThumbprintAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var certificate = await http.Connection.GetClientCertificateAsync(http.RequestAborted);

        return certificate is null
            ? null
            : Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
    }
}
