namespace Sankore.Modules.Integration.Features.RelayAgents;

using Sankore.Modules.Integration.Domain;

/// <summary>
/// What an operator's screen sees of a relay agent (INT-27, criterion 3: last contact, version,
/// state).
///
/// <para>
/// <b>No certificate thumbprint, and no enrolment token hash.</b> Neither is a secret in the
/// sense a password is, and that is precisely why leaving them out needs saying: the thumbprint
/// is the identifier the agent channel authenticates on, so anybody holding it plus a channel
/// that ever trusted a thumbprint alone would be able to impersonate the agent. A read endpoint
/// under <c>Integration.Connection.Manage</c> has no reason to hand it out — an operator needs to
/// know whether the agent is up, not what it authenticates with. A test pins the absence, because
/// adding "the thumbprint, just for support" to a DTO is a one-line change nobody would question.
/// </para>
///
/// <para>
/// <see cref="EnrolmentTokenExpiresAt"/> is a timestamp and not a token: it is what lets a screen
/// say "this agent was registered 40 minutes ago and never came to collect its certificate", which
/// is the one diagnosis an operator cannot make otherwise. The token itself was returned once, by
/// the call that minted it, and is not retrievable by any endpoint — a GET that could show it
/// again would turn this table into a credential store.
/// </para>
/// </summary>
/// <param name="Status">
/// Pending (a token is armed and unused), Active (admitted, may connect) or Revoked.
/// </param>
/// <param name="IsEnrolmentPending">
/// Whether the agent is still waiting to exchange a token that has not expired. Computed rather
/// than read off the status, because a Pending agent whose token has run out is a different
/// operational situation — it needs a new registration, not patience.
/// </param>
/// <param name="LastHeartbeatAt">
/// Last contact. Null means the agent has never reported in since it was admitted.
/// </param>
/// <param name="ReportedLatencyMs">
/// What the agent measured towards the systems it relays, as the agent reported it. Our view of
/// the agent's own latency is the call journal's business (INT-08), not this one's.
/// </param>
internal sealed record RelayAgentDto(
    Guid Id,
    string Name,
    RelayAgentStatus Status,
    bool IsEnrolmentPending,
    DateTimeOffset? EnrolmentTokenExpiresAt,
    DateTimeOffset? CertificateIssuedAt,
    DateTimeOffset? LastHeartbeatAt,
    string? ReportedVersion,
    int? ReportedLatencyMs,
    string? ReportedStatusDetail,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt)
{
    /// <summary>
    /// Projected from the aggregate. Takes the clock so "is the enrolment still pending" is
    /// answered against the same instant for every row of a listing, rather than once per row.
    /// </summary>
    internal static RelayAgentDto From(IntegrationRelayAgent agent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return new RelayAgentDto(
            Id: agent.Id,
            Name: agent.Name,
            Status: agent.Status,
            IsEnrolmentPending: agent.Status == RelayAgentStatus.Pending
                                && agent.EnrolmentTokenExpiresAt > now,
            EnrolmentTokenExpiresAt: agent.EnrolmentTokenExpiresAt,
            CertificateIssuedAt: agent.CertificateIssuedAt,
            LastHeartbeatAt: agent.LastHeartbeatAt,
            ReportedVersion: agent.ReportedVersion,
            ReportedLatencyMs: agent.ReportedLatencyMs,
            ReportedStatusDetail: agent.ReportedStatusDetail,
            CreatedAt: agent.CreatedAt,
            RevokedAt: agent.RevokedAt);
    }
}
