namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A CRM user and their account at the provider (TEL-B-05).
///
/// <para>
/// One to one in both directions, and the unique indexes say so: two CRM users sharing a provider
/// account would attribute each other's calls, and one user holding two accounts would make
/// "which handset do I ring" unanswerable.
/// </para>
/// </summary>
public sealed class TelUserLink : AggregateRoot
{
    private TelUserLink() { }

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid CrmUserId { get; private set; }

    public string ExternalUserId { get; private set; } = default!;

    /// <summary>
    /// The address the provider lists for that account. Kept because it is what the proposed
    /// pairing was based on, so an administrator reviewing a link can see why it was suggested.
    /// </summary>
    public string? ExternalEmail { get; private set; }

    /// <summary>
    /// The number this user's outbound calls present. Null falls back to the connection's own
    /// default — a user with no preference is the ordinary case, not an incomplete one.
    /// </summary>
    public string? DefaultCallerNumber { get; private set; }

    public TelUserLinkStatus Status { get; private set; }

    /// <summary>Why the link stopped working, when it did. Read by an administrator.</summary>
    public string? StatusReason { get; private set; }

    public Guid LinkedBy { get; private set; }
    public DateTimeOffset LinkedAt { get; private set; }
    public uint Version { get; private set; }

    public static TelUserLink Create(
        Guid tenantId,
        Guid connectionId,
        Guid crmUserId,
        string externalUserId,
        Guid linkedBy,
        DateTimeOffset now,
        string? externalEmail = null,
        string? defaultCallerNumber = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (crmUserId == Guid.Empty) throw new DomainException("CrmUserId is required.");

        if (string.IsNullOrWhiteSpace(externalUserId))
            throw new DomainException("A link must name the provider's user.");

        return new TelUserLink
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CrmUserId = crmUserId,
            ExternalUserId = externalUserId.Trim(),
            ExternalEmail = string.IsNullOrWhiteSpace(externalEmail) ? null : externalEmail.Trim(),
            DefaultCallerNumber = string.IsNullOrWhiteSpace(defaultCallerNumber)
                ? null
                : defaultCallerNumber.Trim(),
            Status = TelUserLinkStatus.Active,
            LinkedBy = linkedBy,
            LinkedAt = now,
        };
    }

    /// <summary>
    /// Confirms this pairing again — the repair path, and the reason unlinking may be logical.
    ///
    /// <para>
    /// Answers <c>true</c> when something actually changed, so the caller logs a revival rather
    /// than a confirmation of what was already true. The address is refreshed when one is given
    /// and left alone otherwise: a provider that stopped publishing addresses must not erase the
    /// one that explains why the pairing was proposed in the first place.
    /// </para>
    ///
    /// <para>
    /// <see cref="LinkedBy"/> and <see cref="LinkedAt"/> move to whoever confirmed it now. The
    /// question an audit asks is who vouches for this attribution TODAY, and the original
    /// signature belongs to a link that was deliberately switched off.
    /// </para>
    /// </summary>
    public bool Relink(Guid linkedBy, DateTimeOffset now, string? externalEmail = null)
    {
        var changed = Status != TelUserLinkStatus.Active;

        Status = TelUserLinkStatus.Active;
        StatusReason = null;
        LinkedBy = linkedBy;
        LinkedAt = now;

        if (!string.IsNullOrWhiteSpace(externalEmail) && Blank(externalEmail) != ExternalEmail)
        {
            ExternalEmail = Blank(externalEmail);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// The account vanished or was disabled at the provider (TEL-B-05). Not a delete: the agent's
    /// past calls still point at this row, and it is what explains why nothing new is attributed.
    /// </summary>
    public void MarkBroken(string reason)
    {
        Status = TelUserLinkStatus.Broken;
        StatusReason = Blank(reason);
    }

    /// <summary>
    /// The account is back and usable — the nightly check undoing its own verdict (TEL-B-05).
    ///
    /// <para>
    /// Deliberately NOT <see cref="Relink"/>. The human confirmation that created this pairing is
    /// still on record and still stands, so <see cref="LinkedBy"/> and <see cref="LinkedAt"/> must
    /// not move: stamping SYSTEM on them would erase the one signature an audit asks for, to
    /// record a fact — "the account exists again" — that is not an attribution anybody vouched
    /// for.
    /// </para>
    ///
    /// <para>
    /// Refuses a <see cref="TelUserLinkStatus.Disabled"/> link, which is the whole reason this is
    /// a separate method: somebody unlinked that one on purpose, with a reason, and a nightly
    /// sweep finding the provider account still listed is not an argument against their decision.
    /// Answers false when there was nothing to restore, so the job counts what it did.
    /// </para>
    /// </summary>
    public bool Restore()
    {
        if (Status != TelUserLinkStatus.Broken) return false;

        Status = TelUserLinkStatus.Active;
        StatusReason = null;

        return true;
    }

    /// <summary>Switched off here — the CRM user left, or an administrator unlinked them.</summary>
    public Result Disable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(TelephonyErrors.UnlinkReasonRequired);

        Status = TelUserLinkStatus.Disabled;
        StatusReason = reason.Trim();

        return Result.Ok();
    }

    public void SetDefaultCallerNumber(string? number)
        => DefaultCallerNumber = Blank(number);

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One agent's request to place a call (TEL-B-06).
///
/// <para>
/// Its own row rather than a status on <see cref="TelCall"/>, because for the first seconds there
/// IS no call: the provider has been asked to ring the agent's handset and nothing has happened
/// yet. The row is also what correlates the provider's eventual call back to the record the agent
/// was looking at — the only certain attachment this module ever gets.
/// </para>
/// </summary>
public sealed class TelClickToCall : AggregateRoot
{
    private TelClickToCall() { }

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }
    public Guid CrmUserId { get; private set; }

    public string EntityType { get; private set; } = default!;
    public Guid EntityId { get; private set; }

    /// <summary>Which of the record's numbers was dialled. The front sends this, never a number.</summary>
    public string PhoneSlot { get; private set; } = default!;

    /// <summary>The number actually dialled, encrypted. Kept for the correlation window.</summary>
    public byte[] TargetNumberEnc { get; private set; } = default!;

    public byte[] TargetNumberBi { get; private set; } = default!;

    public TelClickToCallStatus Status { get; private set; }

    /// <summary>The provider's reference for the callback, when it returns one.</summary>
    public string? ExternalRef { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    /// <summary>
    /// Two minutes after the request. Past it the request is <see cref="TelClickToCallStatus.Expired"/>
    /// and no longer correlates: a call that reached the provider five minutes later is a second
    /// attempt, and attaching it here would file it against a record the agent has left.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public uint Version { get; private set; }

    /// <summary>How long a request stays correlatable. §4.2's two minutes, named once.</summary>
    public static readonly TimeSpan CorrelationWindow = TimeSpan.FromMinutes(2);

    public static TelClickToCall Request(
        Guid tenantId,
        Guid connectionId,
        Guid crmUserId,
        string entityType,
        Guid entityId,
        string phoneSlot,
        byte[] targetNumberEnc,
        byte[] targetNumberBi,
        DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (crmUserId == Guid.Empty) throw new DomainException("CrmUserId is required.");
        if (entityId == Guid.Empty) throw new DomainException("EntityId is required.");

        if (!TelEntityTypes.IsKnown(entityType))
            throw new DomainException($"Unknown entity type '{entityType}'.");

        return new TelClickToCall
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            CrmUserId = crmUserId,
            EntityType = entityType,
            EntityId = entityId,
            PhoneSlot = phoneSlot,
            TargetNumberEnc = targetNumberEnc,
            TargetNumberBi = targetNumberBi,
            Status = TelClickToCallStatus.Requested,
            RequestedAt = now,
            ExpiresAt = now.Add(CorrelationWindow),
        };
    }

    public void MarkAgentRinging(string? externalRef)
    {
        Status = TelClickToCallStatus.AgentRinging;
        ExternalRef = string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim();
    }

    public void MarkConnected() => Status = TelClickToCallStatus.Connected;

    public void MarkFailed(string errorCode)
    {
        Status = TelClickToCallStatus.Failed;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Answers false when there was nothing to expire, so the sweep counts what it did rather
    /// than what it looked at.
    /// </summary>
    public bool Expire(DateTimeOffset now)
    {
        if (Status is not (TelClickToCallStatus.Requested or TelClickToCallStatus.AgentRinging))
            return false;

        if (now < ExpiresAt) return false;

        Status = TelClickToCallStatus.Expired;
        return true;
    }

    public bool IsCorrelatableAt(DateTimeOffset now)
        => Status is TelClickToCallStatus.Requested or TelClickToCallStatus.AgentRinging
           && now < ExpiresAt;
}

/// <summary>
/// One event received from the provider (TEL-B-07).
///
/// <para>
/// <b>The row IS the idempotency.</b> The unique index on the deduplication key is what makes a
/// redelivery a no-op: the insert is the check, so two concurrent deliveries both try and the one
/// that loses the race stops there. Nothing else in this module knows how to tell a replay from a
/// first delivery.
/// </para>
///
/// <para>
/// The body is stored ENCRYPTED and nothing logs it: a provider's event carries both numbers of
/// a call in clear, and a table an operator can read over somebody's shoulder is the one place
/// those must not sit. Partitioned by month, like <c>integration_call_log</c> and for the same
/// reason — it is the highest-volume table of the family.
/// </para>
/// </summary>
public sealed class TelWebhookEvent : AggregateRoot
{
    private TelWebhookEvent() { }

    public Guid Id { get; private set; }
    public Guid ConnectionId { get; private set; }

    /// <summary>
    /// The provider's event id when it gives one, otherwise call + type + instant. Composed by
    /// the caller because only the adapter knows which the provider offers.
    /// </summary>
    public string DedupKey { get; private set; } = default!;

    public string EventType { get; private set; } = default!;

    public byte[] PayloadEnc { get; private set; } = default!;

    /// <summary>The partition key. Always the moment of RECEIPT, never a time from the payload.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>What processing concluded — short, and read by an operator chasing a gap.</summary>
    public string? Outcome { get; private set; }

    public static TelWebhookEvent Receive(
        Guid tenantId,
        Guid connectionId,
        string dedupKey,
        string eventType,
        byte[] payloadEnc,
        DateTimeOffset receivedAt)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");

        if (string.IsNullOrWhiteSpace(dedupKey))
            throw new DomainException("A webhook event must carry a deduplication key.");

        return new TelWebhookEvent
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            DedupKey = dedupKey.Trim(),
            EventType = string.IsNullOrWhiteSpace(eventType) ? "unknown" : eventType.Trim(),
            PayloadEnc = payloadEnc,
            ReceivedAt = receivedAt,
        };
    }

    public void MarkProcessed(string outcome, DateTimeOffset at)
    {
        ProcessedAt = at;
        Outcome = outcome.Length > 200 ? outcome[..200] : outcome;
    }
}

/// <summary>
/// What SANKORE holds of one call's recording (TEL-B-16).
///
/// <para>
/// The legal basis and the end of retention are frozen HERE, at retrieval, and never re-read from
/// the tenant's settings afterwards. A policy an institution changes in March must not silently
/// extend the retention of a recording made in January — what was lawful then is what governs it.
/// </para>
/// </summary>
public sealed class TelRecording : AggregateRoot
{
    private TelRecording() { }

    public Guid Id { get; private set; }
    public Guid CallId { get; private set; }

    public TelRecordingStatus Status { get; private set; }

    /// <summary>The encrypted store's own reference. Opaque, never derived from the call.</summary>
    public string? StorageRef { get; private set; }

    /// <summary>Digest of the PLAINTEXT, so a later read can prove the file is the one stored.</summary>
    public byte[]? Sha256 { get; private set; }

    public int? DurationSeconds { get; private set; }

    /// <summary>Why this institution is allowed to hold it. Required, and frozen at retrieval.</summary>
    public string LegalBasis { get; private set; } = default!;

    public DateTimeOffset RetentionUntil { get; private set; }

    /// <summary>
    /// Compliance suspended the purge for a dispute. Carried as a flag plus a reason rather than
    /// by pushing <see cref="RetentionUntil"/> out: the original date is the fact, the hold is
    /// the exception, and merging them loses which was which.
    /// </summary>
    public bool LegalHold { get; private set; }

    public string? LegalHoldReason { get; private set; }

    public DateTimeOffset? FetchedAt { get; private set; }
    public DateTimeOffset? PurgedAt { get; private set; }

    /// <summary>When the copy at the provider was deleted. Null when it never was.</summary>
    public DateTimeOffset? ProviderDeletedAt { get; private set; }

    public uint Version { get; private set; }

    public static TelRecording Expect(
        Guid tenantId, Guid callId, string legalBasis, DateTimeOffset retentionUntil)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (callId == Guid.Empty) throw new DomainException("CallId is required.");

        if (string.IsNullOrWhiteSpace(legalBasis))
            throw new DomainException("A recording must carry the legal basis it is kept under.");

        return new TelRecording
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CallId = callId,
            Status = TelRecordingStatus.Pending,
            LegalBasis = legalBasis.Trim(),
            RetentionUntil = retentionUntil,
        };
    }

    public void MarkStored(string storageRef, byte[] sha256, int? durationSeconds, DateTimeOffset at)
    {
        StorageRef = storageRef;
        Sha256 = sha256;
        DurationSeconds = durationSeconds;
        Status = TelRecordingStatus.Available;
        FetchedAt = at;
    }

    public void MarkFailed() => Status = TelRecordingStatus.Failed;

    public void MarkDeletedAtProvider(DateTimeOffset at) => ProviderDeletedAt = at;

    /// <summary>
    /// Answers false when the purge must not happen — already purged, or held for a dispute.
    /// The caller deletes the bytes only when this said yes.
    /// </summary>
    public bool Purge(DateTimeOffset at)
    {
        if (Status == TelRecordingStatus.Purged) return false;
        if (LegalHold) return false;

        Status = TelRecordingStatus.Purged;
        StorageRef = null;
        PurgedAt = at;

        return true;
    }

    public void PlaceLegalHold(string reason) => (LegalHold, LegalHoldReason) = (true, reason.Trim());

    public void LiftLegalHold() => (LegalHold, LegalHoldReason) = (false, null);
}

/// <summary>
/// One call outcome of one tenant's referential (TEL-B-11).
///
/// <para>
/// Per tenant and not a platform enum, because what a branch network calls the end of a call is
/// its own vocabulary — and because two of the codes carry behaviour: one counts as a contact for
/// the SLA, another demands a callback date. A hard-coded list could not express either.
/// </para>
/// </summary>
public sealed class TelCallOutcome : AggregateRoot
{
    private TelCallOutcome() { }

    public string Code { get; private set; } = default!;
    public string Label { get; private set; } = default!;

    /// <summary>Whether choosing this outcome means the customer was actually reached.</summary>
    public bool CountsAsContact { get; private set; }

    public bool RequiresCallbackDate { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>
    /// Seeded by the platform. A tenant may deactivate one, never delete it: a call wrapped up
    /// last year points at the code, and removing it would leave that call explained by nothing.
    /// </summary>
    public bool IsSystem { get; private set; }

    public int DisplayOrder { get; private set; }

    public static TelCallOutcome Create(
        Guid tenantId,
        string code,
        string label,
        bool countsAsContact,
        bool requiresCallbackDate,
        int displayOrder,
        bool isSystem = false)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (string.IsNullOrWhiteSpace(code)) throw new DomainException("Code is required.");
        if (string.IsNullOrWhiteSpace(label)) throw new DomainException("Label is required.");

        return new TelCallOutcome
        {
            TenantId = tenantId,
            Code = code.Trim().ToUpperInvariant(),
            Label = label.Trim(),
            CountsAsContact = countsAsContact,
            RequiresCallbackDate = requiresCallbackDate,
            DisplayOrder = displayOrder,
            IsActive = true,
            IsSystem = isSystem,
        };
    }

    public void Rename(string label) => Label = label.Trim();

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}
