namespace Sankore.Modules.Integration.Features.RelayAgents.RevokeRelayAgent;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Clears the certificate and records who revoked it.
///
/// <para>
/// <b>What "immediately" means, exactly.</b> The aggregate clears
/// <c>CertificateThumbprint</c>, so from the instant this commits
/// <see cref="IRelayAgentAdmission.AdmitAsync"/> answers <c>null</c> for that certificate. It does
/// not and cannot close a socket that is already open: the socket belongs to whatever hosts the
/// agent channel (INT-26). Criterion 2 is therefore delivered by two halves, and only one of them
/// is in this folder — the channel must re-check admission on every message. That is written on
/// <see cref="IRelayAgentAdmission"/> and reported as a wiring obligation rather than assumed.
/// </para>
/// </summary>
internal sealed class RevokeRelayAgentHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<RevokeRelayAgentHandler> logger)
    : IRequestHandler<RevokeRelayAgentCommand, Result>
{
    public async Task<Result> Handle(RevokeRelayAgentCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // No tenant predicate and no IgnoreQueryFilters: the DbContext's global query filter does
        // the scoping, so another tenant's agent reads as absent and answers 404 — never 403.
        // A 403 would confirm that the id names a real agent somewhere on the platform, which is
        // an inventory of which institutions run an on-premise relay.
        //
        // AsTracking because this mutates.
        var agent = await db.RelayAgents
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == cmd.AgentId, ct);

        if (agent is null)
            return Result.Fail(IntegrationErrors.RelayAgentNotFound);

        // Already revoked: the caller's intent is satisfied, and re-revoking would overwrite the
        // original actor and timestamp — losing the only record of who cut the agent off.
        if (agent.Status == RelayAgentStatus.Revoked)
            return Result.Ok();

        agent.Revoke(currentUser.Id, clock);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail(IntegrationErrors.ConcurrencyConflict);
        }

        // Worth an INFORMATION line of its own: from here on every command this tenant routes
        // through the agent answers INTEGRATION_RELAY_UNAVAILABLE, and the first question asked
        // when that happens is when the agent was cut off, and by whom.
        logger.LogInformation(
            "Relay agent {AgentId} ({AgentName}) of tenant {TenantId} revoked by {ActorId}: "
            + "its certificate is no longer admitted",
            agent.Id, agent.Name, currentUser.TenantId, currentUser.Id);

        return Result.Ok();
    }
}
