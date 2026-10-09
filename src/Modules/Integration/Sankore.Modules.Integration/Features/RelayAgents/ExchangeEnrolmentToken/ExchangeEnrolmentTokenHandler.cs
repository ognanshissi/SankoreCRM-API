namespace Sankore.Modules.Integration.Features.RelayAgents.ExchangeEnrolmentToken;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Resolves the token, admits the agent, and burns the token.
///
/// <para>
/// <b>Every refusal answers the same code.</b> Unknown token, expired token, already-exchanged
/// token, revoked agent, a certificate already bound to another agent, and a lost race against a
/// concurrent exchange all return
/// <see cref="IntegrationErrors.RelayEnrolmentNotPending"/>. A distinguishable error here is an
/// enumeration oracle: told apart, "expired" confirms that a token existed and "not pending"
/// confirms that an agent did, which turns an anonymous endpoint into a way to probe the
/// platform's agent inventory. <c>IntegrationRelayAgent.Admit</c> does return
/// <c>RelayEnrolmentExpired</c> — correctly, it is a domain distinction — and this handler
/// collapses it on the way out. The aggregate keeps the truth; the wire gets one answer.
/// </para>
/// </summary>
internal sealed class ExchangeEnrolmentTokenHandler(
    IntegrationDbContext db,
    TimeProvider clock,
    ILogger<ExchangeEnrolmentTokenHandler> logger)
    : IRequestHandler<ExchangeEnrolmentTokenCommand, Result<RelayAgentAdmissionDto>>
{
    public async Task<Result<RelayAgentAdmissionDto>> Handle(
        ExchangeEnrolmentTokenCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // Trimmed: a token read out of a mail or pasted into an installer prompt arrives with
        // whitespace around it more often than not, and an untrimmed one would be refused with
        // the same opaque answer as a forged one — a support call neither end could diagnose.
        var presentedHash = RelayEnrolmentToken.Hash(cmd.Token.Trim());

        // IgnoreQueryFilters is MANDATORY: the presenter has no tenant context — establishing it
        // is what this call does — so the global filter would compare TenantId against Guid.Empty
        // and no token would ever resolve. The lookup is therefore across tenants, which is
        // precisely why ux_integration_relay_agent_enrolment_token is not tenant-scoped, and it is
        // safe because the token is 256 bits of entropy and the only thing that selects the row.
        //
        // AsTracking because this mutates the aggregate.
        var agent = await db.RelayAgents
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(a => a.EnrolmentTokenHash == presentedHash, ct);

        // An exchanged or revoked agent has its hash cleared to null, so neither can be found by
        // this equality at all: "unknown token" already covers both.
        if (agent is null)
            return Refuse("no armed enrolment token matches the value presented");

        // The authoritative comparison, in constant time. The equality above used the index — it
        // had to, the token is all the presenter has — and this is what the decision rests on.
        if (!RelayEnrolmentToken.Matches(presentedHash, agent.EnrolmentTokenHash))
            return Refuse("the presented token did not match on verification");

        var thumbprint = RelayCertificateThumbprint.Normalize(cmd.CertificateThumbprint);

        // The aggregate owns the state machine: it refuses a non-Pending status and an expired
        // window, clears the hash on success, and that clearing is what makes the token
        // single-use.
        var admitted = agent.Admit(thumbprint, clock.GetUtcNow(), clock);

        if (admitted.IsFailure)
            return Refuse($"the aggregate refused the exchange ({admitted.Error})");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two agents presenting the same token at once: one commits, the other arrives here.
            // Collapsed into the single refusal rather than reported as a conflict — a 409 would
            // tell the loser that the token was real, which is the one thing a refusal must not
            // say.
            return Refuse("a concurrent exchange of the same token won the race");
        }
        catch (DbUpdateException ex) when (ex.IsCertificateAlreadyBound())
        {
            // ux_integration_relay_agent_certificate: the presented certificate is already bound
            // to an agent — possibly in another tenant. Refused with the uniform code, because
            // reporting "already bound" would tell an unauthenticated caller that a certificate it
            // holds is known to this platform.
            //
            // Logged LOUDLY all the same, because this is not a routine race. Either an agent is
            // retrying after a lost answer with a token that has in the meantime been burned, or
            // somebody is trying to bind a certificate they have seen somewhere else — and that
            // second reading is an attempt to become an agent that already exists. The agent and
            // the tenant are named so an operator can look at the right installation; the
            // thumbprint is not, because it is what the channel authenticates on.
            logger.LogWarning(
                "Relay agent enrolment for agent {AgentId} of tenant {TenantId} presented a "
                + "client certificate that is ALREADY BOUND to an agent. Refused. This is either "
                + "a retry of an exchange whose answer was lost, or an attempt to bind a "
                + "certificate belonging to another agent.",
                agent.Id, agent.TenantId);

            // Detached so the failed insert does not poison the rest of the unit of work, the way
            // IntegrationInboxGuard does after its own unique violation.
            db.ChangeTracker.Clear();

            return Result<RelayAgentAdmissionDto>.Fail(IntegrationErrors.RelayEnrolmentNotPending);
        }

        // Information level, with the identity this call established. The thumbprint is not logged:
        // it is what the channel will authenticate on.
        logger.LogInformation(
            "Relay agent {AgentId} ({AgentName}) of tenant {TenantId} exchanged its enrolment "
            + "token and pinned a client certificate",
            agent.Id, agent.Name, agent.TenantId);

        return Result.Ok(new RelayAgentAdmissionDto(
            AgentId: agent.Id,
            TenantId: agent.TenantId,
            Name: agent.Name,
            CertificateIssuedAt: agent.CertificateIssuedAt!.Value));
    }

    /// <summary>
    /// One exit for every refusal, so no future edit can return a second code by accident.
    ///
    /// <para>
    /// The reason is written to the log — where an operator holding the agent's installation
    /// output can see it — and never to the response, which carries only
    /// <see cref="IntegrationErrors.RelayEnrolmentNotPending"/>. Warning level: on a healthy
    /// installation a refused exchange does not happen, and when it does it is either a stale
    /// token or somebody guessing.
    /// </para>
    /// </summary>
    private Result<RelayAgentAdmissionDto> Refuse(string reason)
    {
        logger.LogWarning("A relay agent enrolment exchange was refused: {Reason}.", reason);

        return Result<RelayAgentAdmissionDto>.Fail(IntegrationErrors.RelayEnrolmentNotPending);
    }
}
