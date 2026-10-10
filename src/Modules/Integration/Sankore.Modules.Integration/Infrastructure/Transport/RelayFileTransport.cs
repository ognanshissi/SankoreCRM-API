namespace Sankore.Modules.Integration.Infrastructure.Transport;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// <see cref="IIntegrationFileTransport"/> routed through the on-premise agent — the INDIRECT
/// half of INT-24 criterion 4, "le dépôt se fait par SFTP, directement ou via l'agent relais".
///
/// <para>
/// It exists as its own type, resolved by <see cref="IntegrationFileTransportRouter"/>, because
/// the two routes are the same operation over different carriers and the caller must not branch
/// on which: a batch connection whose IMF exposes no inbound port hands its bytes to an agent
/// that already holds the only connection into that network, and nothing in the outbound slice
/// should know the difference.
/// </para>
///
/// <para>
/// <b>What it does today, and why that is the honest implementation.</b> INT-26/INT-27 — the
/// agent's command channel and its enrolment — are not delivered: <c>integration_relay_agent</c>
/// holds the registry and <c>IntegrationRelayAgent</c> the lifecycle, but there is no channel to
/// push an order down, and the agent host project (<c>Sankore.Integration.RelayAgent</c>) carries
/// no code at all. So this transport performs every check it CAN perform and then answers
/// <see cref="IntegrationErrors.RelayUnavailable"/> — Transient, so the file stays
/// <c>Generated</c> and the batch job retries it the moment the channel exists.
/// </para>
///
/// <para>
/// The alternative — falling back to a direct SFTP connection — is the one thing it must not do.
/// A connection is routed through a relay precisely because this process has no route into that
/// network; "try it directly anyway" would at best fail slowly and at worst reach a different
/// host that happens to answer on the same name from our side of the internet.
/// </para>
///
/// <para>
/// The <b>tenant check below is INT-27's second obligation</b> (plan §5 ter) and not a
/// redundancy. The first guarantee — that only enrolment may link a connection to an agent — lives
/// in a flow; this one lives in the path that actually moves the data, and the data here is a
/// whole institution's day of customer writes. An agent id that resolved to another tenant's
/// agent would execute this tenant's file inside that tenant's network.
/// </para>
/// </summary>
internal sealed class RelayFileTransport(
    IntegrationDbContext db,
    ILogger<RelayFileTransport> logger) : IIntegrationFileTransport
{
    public async Task<IntegrationResult> PutAsync(
        IntegrationConnection connection, string fileName, byte[] content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var agent = await ResolveAgentAsync(connection, ct);
        if (agent.IsFailure) return Collapse(agent);

        logger.LogWarning(
            "Connection {ConnectionId} is routed through relay agent {AgentId}, but no relay "
            + "command channel is deployed (INT-26/INT-27). {FileName} ({SizeBytes} bytes) stays "
            + "undeposited and will be retried.",
            connection.Id, agent.Value.Id, fileName, content.Length);

        return NoChannel();
    }

    public async Task<IntegrationResult<IReadOnlyList<string>>> ListInboundAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        var agent = await ResolveAgentAsync(connection, ct);
        return agent.IsFailure ? Fail<IReadOnlyList<string>>(agent) : NoChannel<IReadOnlyList<string>>();
    }

    public async Task<IntegrationResult<byte[]>> GetInboundAsync(
        IntegrationConnection connection, string fileName, long maxBytes, CancellationToken ct)
    {
        var agent = await ResolveAgentAsync(connection, ct);
        return agent.IsFailure ? Fail<byte[]>(agent) : NoChannel<byte[]>();
    }

    public async Task<IntegrationResult> ArchiveInboundAsync(
        IntegrationConnection connection, string fileName, CancellationToken ct)
    {
        var agent = await ResolveAgentAsync(connection, ct);
        return agent.IsFailure ? Collapse(agent) : NoChannel();
    }

    /// <summary>
    /// The agent this connection is routed through, having verified that it exists, belongs to
    /// the SAME tenant, and is active.
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> paired with an explicit tenant predicate — the repo-wide rule for
    /// a path that runs inside a background job, where the ambient tenant is not necessarily the
    /// one being processed. Here it is also what makes the cross-tenant check meaningful: relying
    /// on the query filter would turn "another tenant's agent" into "no agent", which reads as a
    /// configuration gap instead of the IDOR attempt it would be.
    /// </para>
    /// </summary>
    private async Task<IntegrationResult<IntegrationRelayAgent>> ResolveAgentAsync(
        IntegrationConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.RelayAgentId is not { } agentId)
            return IntegrationResult.Technical<IntegrationRelayAgent>(
                IntegrationErrors.SettingsInvalid,
                $"Connection {connection.Id} is routed through a relay but carries no agent "
                + "reference; an agent is linked by INT-27's enrolment exchange, never by a "
                + "request body.");

        // An opaque reference with no foreign key, as IntegrationConnection documents: a revoked
        // agent leaves a dangling id, and this reader degrades to "relay unavailable" rather than
        // assuming it resolves.
        var agent = await db.RelayAgents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == agentId, ct);

        if (agent is null)
            return IntegrationResult.Transient<IntegrationRelayAgent>(
                IntegrationErrors.RelayUnavailable,
                "The relay agent this connection points at no longer exists.");

        if (agent.TenantId != connection.TenantId)
        {
            // Logged as an error and answered as "unavailable": the caller learns nothing about
            // whether the id exists elsewhere, while an operator sees the incident.
            logger.LogError(
                "Connection {ConnectionId} of tenant {TenantId} points at relay agent {AgentId}, "
                + "which belongs to another tenant. Refusing to transfer.",
                connection.Id, connection.TenantId, agentId);

            return IntegrationResult.Transient<IntegrationRelayAgent>(
                IntegrationErrors.RelayUnavailable,
                "The relay agent configured for this connection is not usable.");
        }

        if (agent.Status != RelayAgentStatus.Active)
            return IntegrationResult.Transient<IntegrationRelayAgent>(
                IntegrationErrors.RelayUnavailable,
                $"Relay agent '{agent.Name}' is {agent.Status}; it holds no session to relay "
                + "through.");

        return IntegrationResult.Ok(agent);
    }

    /// <summary>
    /// Transient, deliberately. The command and the file are both perfectly valid and the
    /// configuration is right — the carrier is simply not deployed yet, so the file waits rather
    /// than being rejected. Same reading of the three families as
    /// <c>UnavailableBatchFileEnlister</c>.
    /// </summary>
    private static IntegrationResult NoChannel()
        => IntegrationResult.Transient(
            IntegrationErrors.RelayUnavailable,
            "No relay command channel is deployed in this installation (INT-26/INT-27); the file "
            + "stays undeposited and is retried.");

    private static IntegrationResult<T> NoChannel<T>()
        => IntegrationResult.Transient<T>(
            IntegrationErrors.RelayUnavailable,
            "No relay command channel is deployed in this installation (INT-26/INT-27).");

    private static IntegrationResult Collapse<T>(IntegrationResult<T> result)
        => result.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional(result.Code!, result.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical(result.Code!, result.Detail),
            _ => IntegrationResult.Transient(result.Code!, result.Detail),
        };

    private static IntegrationResult<TOut> Fail<TOut>(IntegrationResult source)
        => source.Family switch
        {
            ErrorFamily.Functional => IntegrationResult.Functional<TOut>(source.Code!, source.Detail),
            ErrorFamily.Technical => IntegrationResult.Technical<TOut>(source.Code!, source.Detail),
            _ => IntegrationResult.Transient<TOut>(source.Code!, source.Detail),
        };
}
