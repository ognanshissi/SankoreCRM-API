namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>Where an enrolled relay agent stands (INT-27).</summary>
public enum RelayAgentStatus
{
    /// <summary>An enrolment token has been issued and not yet exchanged.</summary>
    Pending,

    /// <summary>The token was exchanged for a certificate; the agent may connect.</summary>
    Active,

    /// <summary>Revoked. The session is cut and the certificate no longer admitted.</summary>
    Revoked
}

/// <summary>
/// One on-premise relay agent of one tenant (INT-26, INT-27).
///
/// <para>
/// <b>This table is an addition to the schema the specification lists</b>, and INT-27 requires it:
/// a single-use enrolment token, a certificate to admit or refuse, a revocation that takes effect
/// immediately, and heartbeats exposed through the API all need somewhere to live.
/// <c>integration_connection.relay_agent_id</c> points at a row this table owns; the specification
/// names that column without naming what it points at.
/// </para>
///
/// <para>
/// <b>The security rule this type exists to enforce</b> (see docs/integration-module-plan.md §5bis):
/// the link between a connection and an agent is established HERE, by the enrolment exchange, and
/// never by accepting an agent id in a request body. The relay executes orders inside the IMF's own
/// network — local HTTP, SFTP, read-only SQL — so an id a tenant could choose would let one tenant
/// route its writes through another tenant's network and read its directories.
/// </para>
///
/// <para>
/// The enrolment token is stored HASHED, never in clear. It is a bearer credential for the few
/// minutes it lives, and a leaked database backup must not hand somebody an agent identity; the
/// same reasoning M12 applies to every other credential, except that this one cannot live in the
/// vault because it is looked up BY its own value.
/// </para>
/// </summary>
public sealed class IntegrationRelayAgent : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Operator-facing name, so two sites of one IMF can be told apart.</summary>
    public string Name { get; private set; } = string.Empty;

    public RelayAgentStatus Status { get; private set; }

    /// <summary>
    /// SHA-256 of the enrolment token, lower-case hex. Null once exchanged or revoked — a
    /// single-use token that survives its use is no longer single-use.
    /// </summary>
    public string? EnrolmentTokenHash { get; private set; }

    public DateTimeOffset? EnrolmentTokenExpiresAt { get; private set; }

    /// <summary>
    /// SHA-256 thumbprint of the client certificate admitted for this agent. The certificate
    /// itself is never stored: a thumbprint is all that is needed to admit or refuse one, and
    /// holding the material would make this table worth stealing.
    /// </summary>
    public string? CertificateThumbprint { get; private set; }

    public DateTimeOffset? CertificateIssuedAt { get; private set; }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    /// <summary>Version the agent last reported, so a stale deployment is visible.</summary>
    public string? ReportedVersion { get; private set; }

    /// <summary>
    /// The HIGHEST latency among the targets the agent last reported, in milliseconds.
    ///
    /// <para>
    /// A single number alongside <see cref="ReportedTargetsJson"/> is not a second source of
    /// truth: it is derived from that array at write time and exists so a list of agents can be
    /// sorted and filtered in SQL without opening the jsonb. The <b>highest</b> rather than an
    /// average, because what an operator needs to see is the worst link — an agent whose CBS
    /// answers in 40 ms and whose SFTP server answers in nine seconds is not a healthy agent, and
    /// an average would say it was.
    /// </para>
    /// </summary>
    public int? ReportedLatencyMs { get; private set; }

    /// <summary>
    /// Reachability and latency of EACH relayed system, as the agent sent them.
    ///
    /// <para>
    /// Criterion 5 of INT-26 asks for "la latence vers <b>chaque</b> système relié", and a single
    /// column cannot hold that: an agent relays a CBS, an SFTP server and possibly a read-only
    /// view, each with its own reachability. Keeping only one number was a defect of this entity —
    /// the heartbeat arrived complete and was truncated on the way in.
    /// </para>
    ///
    /// <para>
    /// jsonb and not a child table: this is read whole, for one agent, to render one panel.
    /// Nothing queries across agents' targets, and a child table would buy joins nobody performs
    /// at the cost of a delete-and-reinsert on every single heartbeat — which, at one heartbeat a
    /// minute per agent, is the most frequent write this module makes.
    /// </para>
    ///
    /// <para>Names and kinds of declared targets only. Never a URL, a host or a credential.</para>
    /// </summary>
    public string? ReportedTargetsJson { get; private set; }

    /// <summary>What the agent said about itself. Never personal data.</summary>
    public string? ReportedStatusDetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }

    public uint Version { get; private set; }

    private IntegrationRelayAgent() { }

    /// <summary>
    /// Registers an agent and arms its single-use enrolment token.
    ///
    /// <para>
    /// The caller keeps the clear token to hand to the IT team; only its hash is stored. Nothing
    /// in this module can show it again, which is the point.
    /// </para>
    /// </summary>
    public static IntegrationRelayAgent Enrol(
        Guid tenantId,
        string name,
        string enrolmentTokenHash,
        DateTimeOffset tokenExpiresAt,
        Guid createdBy,
        TimeProvider clock,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("An agent name is required.");
        if (string.IsNullOrWhiteSpace(enrolmentTokenHash))
            throw new DomainException("An enrolment token hash is required.");

        return new IntegrationRelayAgent
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            Name = name.Trim(),
            Status = RelayAgentStatus.Pending,
            EnrolmentTokenHash = enrolmentTokenHash,
            EnrolmentTokenExpiresAt = tokenExpiresAt,
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = createdBy,
        };
    }

    /// <summary>True while the agent may open a session.</summary>
    public bool IsAdmitted => Status == RelayAgentStatus.Active;

    /// <summary>
    /// Exchanges the enrolment token for a certificate.
    ///
    /// <para>
    /// Refuses a token that is expired, already used, or offered to a revoked agent. The token
    /// hash is cleared on success, which is what makes it single-use — a window of a few minutes
    /// is a mitigation, not the guarantee.
    /// </para>
    /// </summary>
    public Result Admit(string certificateThumbprint, DateTimeOffset now, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(certificateThumbprint))
            throw new DomainException("A certificate thumbprint is required.");

        if (Status != RelayAgentStatus.Pending)
            return Result.Fail(PublicApi.IntegrationErrors.RelayEnrolmentNotPending);

        if (EnrolmentTokenExpiresAt is null || EnrolmentTokenExpiresAt <= now)
            return Result.Fail(PublicApi.IntegrationErrors.RelayEnrolmentExpired);

        Status = RelayAgentStatus.Active;
        CertificateThumbprint = certificateThumbprint.Trim().ToLowerInvariant();
        CertificateIssuedAt = clock.GetUtcNow();
        EnrolmentTokenHash = null;
        EnrolmentTokenExpiresAt = null;
        return Result.Ok();
    }

    /// <summary>
    /// Revoked, and never deleted: connections, call logs and batch files point at this row, and a
    /// deleted agent would make a year of audit trail unreadable. The thumbprint is cleared so the
    /// certificate cannot be admitted again even if the row were reactivated by mistake.
    /// </summary>
    public void Revoke(Guid actor, TimeProvider clock)
    {
        Status = RelayAgentStatus.Revoked;
        CertificateThumbprint = null;
        EnrolmentTokenHash = null;
        EnrolmentTokenExpiresAt = null;
        RevokedAt = clock.GetUtcNow();
        RevokedBy = actor;
    }

    /// <summary>
    /// Records what the agent said about itself.
    ///
    /// <para>
    /// <paramref name="targetsJson"/> is the per-target array of criterion 5, stored verbatim;
    /// <paramref name="highestLatencyMs"/> is the aggregate the caller derived from it. The
    /// aggregate is passed in rather than computed here because this aggregate is a presentation
    /// choice — the worst link — and the domain should not be the place that decides it while
    /// pretending to be the place that stores it.
    /// </para>
    /// </summary>
    public void RecordHeartbeat(
        string? version,
        int? highestLatencyMs,
        string? statusDetail,
        string? targetsJson,
        TimeProvider clock)
    {
        LastHeartbeatAt = clock.GetUtcNow();
        ReportedVersion = version?.Trim();
        ReportedLatencyMs = highestLatencyMs is >= 0 ? highestLatencyMs : null;
        ReportedStatusDetail = statusDetail?.Trim();
        ReportedTargetsJson = string.IsNullOrWhiteSpace(targetsJson) ? null : targetsJson;
    }
}
