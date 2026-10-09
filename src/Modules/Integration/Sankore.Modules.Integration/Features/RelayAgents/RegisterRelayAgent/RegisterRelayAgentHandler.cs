namespace Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Mints the token, stores its hash, returns the clear value once.
/// </summary>
internal sealed class RegisterRelayAgentHandler(
    IntegrationDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<RegisterRelayAgentHandler> logger)
    : IRequestHandler<RegisterRelayAgentCommand, Result<RelayAgentEnrolmentDto>>
{
    public async Task<Result<RelayAgentEnrolmentDto>> Handle(
        RegisterRelayAgentCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var tenantId = currentUser.TenantId;

        // Minted here, held in a local, and never written anywhere but the response. The hash is
        // what the aggregate takes — Enrol has no overload that accepts a clear token, so this
        // handler cannot store one by accident.
        var token = RelayEnrolmentToken.Generate();
        var expiresAt = clock.GetUtcNow().Add(RelayEnrolmentToken.Lifetime);

        var agent = IntegrationRelayAgent.Enrol(
            tenantId: tenantId,
            name: cmd.Name,
            enrolmentTokenHash: RelayEnrolmentToken.Hash(token),
            tokenExpiresAt: expiresAt,
            createdBy: currentUser.Id,
            clock: clock);

        db.RelayAgents.Add(agent);

        // No concurrency catch: this is an insert of a freshly minted id, so there is no row for a
        // concurrent writer to have moved. A collision on the token-hash unique index is possible
        // in theory and would surface as an exception rather than a silent success — at 256 bits
        // of entropy it is not a case worth a branch, and a branch that retried would be a branch
        // nothing could ever exercise.
        await db.SaveChangesAsync(ct);

        // The id, the tenant and the expiry. Not the token, not a prefix of it, not its length:
        // a log sink is chosen for volume and retention, not for secrecy, and this one value is
        // enough on its own to take over an agent identity for the next half hour.
        logger.LogInformation(
            "Relay agent {AgentId} ({AgentName}) registered for tenant {TenantId}; "
            + "its enrolment token expires at {ExpiresAt}",
            agent.Id, agent.Name, tenantId, expiresAt);

        return Result.Ok(new RelayAgentEnrolmentDto(
            AgentId: agent.Id,
            Name: agent.Name,
            EnrolmentToken: token,
            ExpiresAt: expiresAt));
    }
}
