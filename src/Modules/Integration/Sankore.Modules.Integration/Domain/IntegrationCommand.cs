namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// One write owed to an external system (INT-05).
///
/// <para>
/// It exists so a consumer module never has to care whether the CBS was reachable. The row is
/// created inside the CALLER's transaction — a client validated while the CBS is down still owes
/// its creation, and the agent is not made to wait for a system SANKORE does not operate.
/// </para>
///
/// <para>
/// Unlike every other aggregate in this solution, an invalid transition here <b>throws</b>
/// rather than returning a <see cref="Result"/>. That is the specification's choice and it is
/// defensible: the only callers are this module's own dispatcher and its replay endpoint, so an
/// out-of-table move is a programming error, not an outcome anybody can act on. A
/// <see cref="Result"/> would invite a handler to log it and carry on with a command whose
/// status no longer matches what happened.
/// </para>
/// </summary>
public sealed class IntegrationCommand : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Which connection executes it. Intra-schema FK — allowed, same module.</summary>
    public Guid ConnectionId { get; private set; }

    public CommandType CommandType { get; private set; }

    /// <summary>CRM entity type, as a string by repo convention. See IntegrationEntityTypes.</summary>
    public string EntityType { get; private set; } = string.Empty;

    /// <summary>The CRM-side identifier. Opaque: no foreign key leaves this schema.</summary>
    public Guid CrmId { get; private set; }

    /// <summary>
    /// Deterministic, derived from what the write IS. A second identical request collides on
    /// <c>ux_integration_commands_tenant_idempotency</c> and the caller is handed this row back
    /// instead of a duplicate write.
    /// </summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>
    /// AES-256-GCM, keyed to this module. A command payload is a customer's identity document,
    /// address and income on its way out of the platform; it is at rest here for as long as the
    /// external system is down.
    /// </summary>
    public string? PayloadEncrypted { get; private set; }

    /// <summary>
    /// The payload's field NAMES, comma-separated, in clear. This is what makes the audit row
    /// useful without making it a copy of the payload: "which fields did we send" is answerable,
    /// "what were their values" is not.
    /// </summary>
    public string? PayloadFieldNames { get; private set; }

    public CommandStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>When a <see cref="CommandStatus.RetryScheduled"/> command becomes due again.</summary>
    public DateTimeOffset? NextAttemptAt { get; private set; }

    public ErrorFamily? LastErrorFamily { get; private set; }

    /// <summary>Code and operator-facing detail. Never a payload, never personal data.</summary>
    public string? LastErrorMessage { get; private set; }

    /// <summary>What the external system answered with on success — its own reference.</summary>
    public string? ExternalResponseRef { get; private set; }

    /// <summary>Set when the command left through a file rather than a call (INT-24).</summary>
    public Guid? BatchFileId { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>PostgreSQL xmin — the optimistic lock the dispatcher claims Sending under.</summary>
    public uint Version { get; private set; }

    private IntegrationCommand() { }

    public static IntegrationCommand Create(
        Guid tenantId,
        Guid connectionId,
        CommandType commandType,
        string entityType,
        Guid crmId,
        IdempotencyKey idempotencyKey,
        Guid createdBy,
        TimeProvider clock,
        string? payloadEncrypted = null,
        IEnumerable<string>? payloadFieldNames = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (connectionId == Guid.Empty) throw new DomainException("ConnectionId is required.");
        if (crmId == Guid.Empty) throw new DomainException("CrmId is required.");
        if (string.IsNullOrWhiteSpace(entityType)) throw new DomainException("EntityType is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey.Value))
            throw new DomainException("An idempotency key is required — it is what makes a replay safe.");

        return new IntegrationCommand
        {
            // The caller may supply the id so an event can reference the command before
            // SaveChanges, as Client.Create and KycFile.Open both allow.
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CommandType = commandType,
            EntityType = entityType.Trim(),
            CrmId = crmId,
            IdempotencyKey = idempotencyKey.Value,
            PayloadEncrypted = payloadEncrypted,
            PayloadFieldNames = payloadFieldNames is null
                ? null
                : string.Join(',', payloadFieldNames.Where(f => !string.IsNullOrWhiteSpace(f)).Order()),
            Status = CommandStatus.Pending,
            Attempts = 0,
            CreatedBy = createdBy,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    // ── Transition table (INT-05, literal) ──────────────────────────────────

    /// <summary>
    /// The specification's table, declarative. Every method below goes through
    /// <see cref="MoveTo"/>; nothing compares statuses by hand.
    /// </summary>
    private static readonly Dictionary<CommandStatus, CommandStatus[]> Allowed = new()
    {
        [CommandStatus.Pending] = [CommandStatus.Sending, CommandStatus.Cancelled],
        [CommandStatus.Sending] =
        [
            CommandStatus.Succeeded, CommandStatus.RetryScheduled,
            CommandStatus.Rejected, CommandStatus.Batched
        ],
        [CommandStatus.Batched] = [CommandStatus.Succeeded, CommandStatus.Rejected],
        [CommandStatus.RetryScheduled] = [CommandStatus.Sending],
        [CommandStatus.Rejected] = [CommandStatus.Pending, CommandStatus.Cancelled],
        [CommandStatus.Succeeded] = [],
        [CommandStatus.Cancelled] = [],
    };

    public bool CanTransitionTo(CommandStatus target)
        => Allowed.TryGetValue(Status, out var targets) && targets.Contains(target);

    /// <summary>True while the command still owes a write.</summary>
    public bool IsOpen => Status
        is CommandStatus.Pending or CommandStatus.Sending
        or CommandStatus.Batched or CommandStatus.RetryScheduled;

    /// <summary>
    /// The default life of a claim. A command that has been <see cref="CommandStatus.Sending"/>
    /// for longer than this is treated as abandoned by a worker that died, not as in flight.
    ///
    /// <para>
    /// Fifteen minutes is a floor, not a tuning: it must exceed the longest plausible call
    /// (<see cref="ConnectionSettings.TimeoutSeconds"/> is capped far below it) so a live worker
    /// is never robbed of its command, and it is short enough that a stranded write is picked up
    /// within one coffee break rather than never.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultClaimWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when the dispatcher may pick it up now.
    ///
    /// <para>
    /// The third branch is not in INT-06, which speaks only of <c>Pending</c> and due
    /// <c>RetryScheduled</c> commands, and it closes a hole that the specification's wording
    /// leaves open: a worker killed mid-call — an OOM, a pod eviction, a deploy — leaves its
    /// command in <see cref="CommandStatus.Sending"/>, and nothing in the specified sweep ever
    /// looks at that status again. The write would be owed for ever, with no error anywhere,
    /// which is the one outcome this queue exists to prevent.
    /// </para>
    ///
    /// <para>
    /// A claim is only stale once <see cref="NextAttemptAt"/> has passed, so a command genuinely
    /// in flight on another worker is never stolen and double-sent.
    /// </para>
    /// </summary>
    public bool IsDueAt(DateTimeOffset now) => Status switch
    {
        CommandStatus.Pending => true,
        CommandStatus.RetryScheduled => NextAttemptAt is null || NextAttemptAt <= now,

        // An expired claim. Null is deliberately NOT due here, unlike RetryScheduled above: a
        // row written before this column carried a claim expiry would otherwise be re-sent on the
        // next sweep while a worker was still on it.
        CommandStatus.Sending => NextAttemptAt is not null && NextAttemptAt <= now,

        _ => false,
    };

    private void MoveTo(CommandStatus target, TimeProvider clock)
    {
        if (!CanTransitionTo(target))
            throw new DomainException(
                $"INTEGRATION_INVALID_TRANSITION: {Status} → {target} is not in the command "
                + $"transition table (command {Id}).");

        Status = target;

        if (target is CommandStatus.Succeeded or CommandStatus.Rejected or CommandStatus.Cancelled)
            CompletedAt = clock.GetUtcNow();
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    /// <summary>
    /// Claimed by a dispatcher job. Increments the attempt count HERE rather than on failure, so
    /// a worker that dies mid-call still consumes its attempt — otherwise a call that reliably
    /// kills the process would be retried for ever.
    /// </summary>
    /// <param name="claimExpiresAfter">
    /// How long this claim is honoured before another sweep may take the command over. Defaults
    /// to <see cref="DefaultClaimWindow"/>.
    /// </param>
    public void BeginSending(TimeProvider clock, TimeSpan? claimExpiresAfter = null)
    {
        MoveTo(CommandStatus.Sending, clock);
        Attempts++;

        // Stamped rather than cleared, which is what makes a stranded claim recoverable. While
        // the status is Sending, next_attempt_at means "when this claim expires" and not "when to
        // retry" — one column, two readings, both honest to its name, and the specified index
        // (status, next_attempt_at) serves each of them.
        NextAttemptAt = clock.GetUtcNow() + (claimExpiresAfter ?? DefaultClaimWindow);
    }

    /// <summary>
    /// Confirmed. <paramref name="externalResponseRef"/> is the external system's own reference —
    /// written in the same transaction as the <c>integration_reference</c> row (INT-07).
    /// </summary>
    public void Succeed(string? externalResponseRef, TimeProvider clock)
    {
        MoveTo(CommandStatus.Succeeded, clock);
        ExternalResponseRef = externalResponseRef;
        LastErrorFamily = null;
        LastErrorMessage = null;
    }

    /// <summary>Left through an outbound file instead of a call. Closed later by an ack.</summary>
    public void MarkBatched(Guid batchFileId, TimeProvider clock)
    {
        if (batchFileId == Guid.Empty) throw new DomainException("A batch file id is required.");

        MoveTo(CommandStatus.Batched, clock);
        BatchFileId = batchFileId;
    }

    /// <summary>
    /// Puts the command back in the queue to wait for a scheduled moment, <b>without spending an
    /// attempt</b>.
    ///
    /// <para>
    /// Distinct from <see cref="ScheduleRetry"/>, and the distinction is the difference between a
    /// batch connection working and silently losing its writes. The attempt budget exists for
    /// failures of the external system; waiting for a cut-off is not a failure and was never an
    /// attempt at anything. Counted as one, a daily cycle spends eight attempts in about three
    /// hours, and every command created in the morning reaches <c>Rejected</c> before its evening
    /// file is written — and the generator's eligible set does not include <c>Rejected</c>, so
    /// that command never leaves at all.
    /// </para>
    ///
    /// <para>
    /// The claim's increment is rolled back rather than skipped, because the dispatcher has
    /// already taken it in <see cref="BeginSending"/> by the time anybody knows the cycle is not
    /// due: the mode is only discoverable after the connection is resolved. Flooring at zero
    /// keeps a deferral from ever lending an attempt back.
    /// </para>
    /// </summary>
    public void DeferUntil(DateTimeOffset dueAt, string code, string? detail, TimeProvider clock)
    {
        MoveTo(CommandStatus.RetryScheduled, clock);

        NextAttemptAt = dueAt;
        Attempts = Attempts > 0 ? Attempts - 1 : 0;

        // Recorded, because an operator looking at a command that has sat in RetryScheduled since
        // this morning needs to see that it is waiting for an hour rather than failing.
        LastErrorFamily = ErrorFamily.Transient;
        LastErrorMessage = Describe(code, detail);
    }

    /// <summary>Transient failure with attempts left: comes back at <paramref name="nextAttemptAt"/>.</summary>
    public void ScheduleRetry(DateTimeOffset nextAttemptAt, string code, string? detail, TimeProvider clock)
    {
        MoveTo(CommandStatus.RetryScheduled, clock);
        NextAttemptAt = nextAttemptAt;
        LastErrorFamily = ErrorFamily.Transient;
        LastErrorMessage = Describe(code, detail);
    }

    /// <summary>
    /// Will not be delivered: the external system refused, our configuration is wrong, or the
    /// attempt budget is spent. The family is kept because it decides what the alert says.
    /// </summary>
    public void Reject(ErrorFamily family, string code, string? detail, TimeProvider clock)
    {
        MoveTo(CommandStatus.Rejected, clock);
        NextAttemptAt = null;
        LastErrorFamily = family;
        LastErrorMessage = Describe(code, detail);
    }

    /// <summary>
    /// Replayed by a human. The attempt counter is reset: an operator who fixed a missing mapping
    /// is starting a new story, and leaving the count at eight would reject the command on its
    /// first new failure.
    /// </summary>
    public void Replay(TimeProvider clock)
    {
        MoveTo(CommandStatus.Pending, clock);
        Attempts = 0;
        NextAttemptAt = null;
        CompletedAt = null;
    }

    /// <summary>Abandoned by a human. Final — and deliberately not available from Succeeded.</summary>
    public void Cancel(TimeProvider clock) => MoveTo(CommandStatus.Cancelled, clock);

    /// <summary>
    /// Replaces the encrypted payload, for a replay after a correction. Field names are
    /// recomputed with it so the audit trail never describes a payload that is no longer there.
    ///
    /// <para>
    /// Callable ONLY from the dispatcher's system re-derivation path, where the new payload is
    /// read back from CRM state — never from an operator-supplied value. A payload edited by hand
    /// would be an unauditable write (INT-08 keeps values out of the audit row by design, so the
    /// trail could not show what changed) and, for <c>DebitAccount</c>, a money movement
    /// authorised by one permission. See <c>ReplayIntegrationCommandCommand</c>.
    /// </para>
    /// </summary>
    public void ReplacePayload(string? payloadEncrypted, IEnumerable<string>? fieldNames)
    {
        PayloadEncrypted = payloadEncrypted;
        PayloadFieldNames = fieldNames is null
            ? null
            : string.Join(',', fieldNames.Where(f => !string.IsNullOrWhiteSpace(f)).Order());
    }

    /// <summary>
    /// Code first, detail second, truncated. The column is bounded and an adapter's detail can
    /// be an entire HTML error page — which would push the code itself out of the row.
    /// </summary>
    private static string Describe(string code, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return code;

        var composed = $"{code}: {detail}";
        return composed.Length <= 1000 ? composed : composed[..1000];
    }
}
