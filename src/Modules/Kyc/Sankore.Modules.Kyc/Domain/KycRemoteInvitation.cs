namespace Sankore.Modules.Kyc.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Lifecycle of one remote-collection link (KRM-B-03/04/05).
///
/// <para>
/// Eight states and not a pair of booleans, because an agent looking at a customer who has not
/// finished needs to know WHERE they stopped: a link nobody opened is a deliverability problem, a
/// link opened but never identified is a customer who does not have their welcome email to hand,
/// and a link locked after five wrong answers is somebody else holding it. Those three call for
/// three different actions.
/// </para>
/// </summary>
public enum KycRemoteInvitationStatus
{
    /// <summary>Created, with the email deliberately queued for later so it lands after the welcome message.</summary>
    Scheduled,

    /// <summary>The message has been handed to M08. Not a proof of delivery — nothing here can prove that.</summary>
    Sent,

    /// <summary>Somebody holding the link asked whether it was still valid. The weakest signal of the eight.</summary>
    Opened,

    /// <summary>Client number and date of birth matched; a session was issued.</summary>
    Identified,

    /// <summary>The customer sent their file. The link is spent from this point on.</summary>
    Submitted,

    /// <summary>Its validity window closed before it was submitted.</summary>
    Expired,

    /// <summary>Withdrawn — superseded by a newer link, or by a change that invalidated it.</summary>
    Revoked,

    /// <summary>Too many failed identification attempts. Only a new link gets the customer moving again.</summary>
    Locked,
}

/// <summary>Why a link was issued.</summary>
public enum KycRemoteInvitationPurpose
{
    /// <summary>The first collection, sent at conversion.</summary>
    Initial,

    /// <summary>A second pass, narrowed to the documents a validator asked to be redone.</summary>
    Complement,
}

/// <summary>
/// A single-use, time-limited link letting a newly converted customer send their identity
/// documents from their own phone (KRM-B-03).
///
/// <para>
/// <b>Only the HASH of the token is stored.</b> The link is a bearer credential: anybody holding it
/// reaches the identification page of one specific KYC file. A database dump, a replicated backup
/// or a careless log must not be enough to walk it — so the random value exists exactly once, in
/// the email, and this table keeps SHA-256 of it. A lookup is a hash and an index probe; there is
/// no way to answer "what was the link of this file" and that is the point.
/// </para>
///
/// <para>
/// <b>The token is minted at SEND time, which is why the hash is nullable.</b> A row is created at
/// the conversion in <see cref="KycRemoteInvitationStatus.Scheduled"/> — so the agent sees
/// immediately that a link is owed — and the email goes out minutes later, after the welcome
/// message. Minting at creation would mean carrying the clear token to the delayed job, and a
/// Hangfire payload is a row in a shared database and a line on a dashboard: exactly the places
/// the hash-only storage exists to keep it out of. The token therefore lives inside one job
/// invocation, between <see cref="AttachTokenAndMarkSent"/> and the handoff to M08.
/// </para>
///
/// <para>
/// <b>No foreign key to <see cref="KycFile"/>, and none to the customer</b> — the same rule every
/// other table of this module follows. <see cref="KycFileId"/> and <see cref="CustomerId"/> are
/// plain ids with an index; the row is tenant-scoped through <see cref="AggregateRoot"/> and the
/// context's global query filter. <see cref="CustomerId"/> is denormalised from the file rather
/// than resolved on read for the reason <see cref="KycFile.AgencyId"/> is: a public endpoint
/// holding only a token has to reach the customer in one indexed read, before any session exists.
/// </para>
///
/// <para>
/// <b>One ACTIVE link per file</b>, guaranteed by the filtered unique index
/// <c>ux_kyc_remote_invitations_active_per_file</c> and not by a read in a handler. Issuing a
/// second link revokes the first, so a customer always has exactly one valid link and an attacker
/// who intercepted an older one holds nothing.
/// </para>
/// </summary>
public sealed class KycRemoteInvitation : AggregateRoot
{
    /// <summary>
    /// The statuses in which the link can still be used, and therefore the ones the "one active
    /// link per file" index covers. Spelled once, here, because the index filter in
    /// <c>KycRemoteInvitationConfiguration</c> has to say the same thing in SQL and the two
    /// drifting apart would either let two links live at once or stop a legitimate re-issue.
    /// </summary>
    public static readonly KycRemoteInvitationStatus[] ActiveStatuses =
    [
        KycRemoteInvitationStatus.Scheduled,
        KycRemoteInvitationStatus.Sent,
        KycRemoteInvitationStatus.Opened,
        KycRemoteInvitationStatus.Identified,
    ];

    public Guid Id { get; private set; }

    /// <summary>Opaque reference to the KYC file this link collects for.</summary>
    public Guid KycFileId { get; private set; }

    /// <summary>Opaque reference to the customer record owned by M01.</summary>
    public Guid CustomerId { get; private set; }

    /// <summary>
    /// SHA-256 of the link's token — 32 bytes, never the token itself.
    ///
    /// <para>
    /// NULL while the row is <see cref="KycRemoteInvitationStatus.Scheduled"/>: the token does not
    /// exist yet. A scheduled link is consequently unreachable by construction rather than by a
    /// status check, since the public lookup is an equality probe on this column and nothing
    /// matches null.
    /// </para>
    /// </summary>
    public byte[]? TokenHash { get; private set; }

    public KycRemoteInvitationStatus Status { get; private set; }
    public KycRemoteInvitationPurpose Purpose { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? OpenedAt { get; private set; }
    public DateTimeOffset? IdentifiedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }

    /// <summary>
    /// Failed identification attempts on this link. On the link and not on the customer, because
    /// the counter has to reset when a new link is issued — locking a CUSTOMER out would hand
    /// anybody holding a stale link the power to freeze a real enrolment.
    /// </summary>
    public int FailedAttempts { get; private set; }

    public DateTimeOffset? LockedAt { get; private set; }

    /// <summary>
    /// How many times the customer retook each photo, keyed by document kind (KRM-B-06). A
    /// document retaken past the tenant's budget is a signal for the validator rather than a
    /// refusal, so the count has to survive the submission.
    /// <para>Written from KRM-B-06 onwards; empty until then.</para>
    /// </summary>
    public string QualityRetriesJson { get; private set; } = "{}";

    /// <summary>
    /// When the customer accepted the processing of their biometric data, and which version of the
    /// text they were shown (KRM-B-06). Both, not just the date: consent to a text nobody can
    /// identify is not evidence of anything.
    /// <para>Written from KRM-B-06 onwards.</para>
    /// </summary>
    public DateTimeOffset? ConsentGivenAt { get; private set; }

    public string? ConsentTextVersion { get; private set; }

    /// <summary>
    /// Who caused the link to exist: <c>Guid.Empty</c> for the SYSTEM account at a conversion, the
    /// agent's id when one re-issues it by hand.
    /// </summary>
    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? RevokedBy { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>
    /// Why the link was withdrawn — an operator's motive or one of the automatic codes, read by an
    /// agent and never a field value. Same category as <c>KycApprovalStep.Comment</c>.
    /// </summary>
    public string? RevokeReason { get; private set; }

    /// <summary>PostgreSQL xmin — optimistic concurrency, as on <see cref="KycFile"/>.</summary>
    public uint Version { get; private set; }

    private KycRemoteInvitation() { }

    /// <summary>
    /// Issues a link for <paramref name="kycFileId"/>, valid for <paramref name="validityHours"/>.
    /// </summary>
    /// <param name="createdBy">
    /// <c>Guid.Empty</c> for the automatic issue at conversion. Allowed here, unlike
    /// <see cref="KycFile.ManuallyValidate"/>, and the difference is the whole point: sending a
    /// link asserts nothing about the customer's identity, so the SYSTEM account may do it.
    /// </param>
    public static KycRemoteInvitation Issue(
        Guid tenantId,
        Guid kycFileId,
        Guid customerId,
        int validityHours,
        Guid createdBy,
        TimeProvider clock,
        KycRemoteInvitationPurpose purpose = KycRemoteInvitationPurpose.Initial,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (kycFileId == Guid.Empty) throw new DomainException("KycFileId is required.");
        if (customerId == Guid.Empty) throw new DomainException("CustomerId is required.");

        if (validityHours < 1)
            throw new DomainException("A link must be valid for at least one hour.");

        var now = clock.GetUtcNow();

        return new KycRemoteInvitation
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            KycFileId = kycFileId,
            CustomerId = customerId,
            // Scheduled and not Sent: the email is queued minutes later on purpose, so it arrives
            // after the welcome message that carries the client number the customer will be asked
            // for. Reporting it as sent now would make an agent chase a delivery that has not been
            // attempted.
            Status = KycRemoteInvitationStatus.Scheduled,
            Purpose = purpose,
            ExpiresAt = now.AddHours(validityHours),
            CreatedBy = createdBy,
            CreatedAt = now,
        };
    }

    /// <summary>True while the link can still be used.</summary>
    public bool IsActive => ActiveStatuses.Contains(Status);

    /// <summary>
    /// Whether the link may still be walked AT <paramref name="now"/> — active and not past its
    /// window.
    ///
    /// <para>
    /// The expiry is checked against the clock and not only against the status, because the status
    /// only becomes <see cref="KycRemoteInvitationStatus.Expired"/> when the nightly sweep runs.
    /// A link whose window closed four hours ago is dead immediately; waiting for a job to say so
    /// would leave a bearer credential usable for up to a day past its stated lifetime.
    /// </para>
    /// </summary>
    public bool IsUsableAt(DateTimeOffset now) => IsActive && now < ExpiresAt;

    /// <summary>
    /// Binds the freshly minted token to this link and records that the email is on its way. ONE
    /// method for the two, deliberately: a hash written without the status moving would leave a
    /// walkable link nobody has been told about, and a status moved without the hash would leave a
    /// link marked sent that can never be used. The pair commits together or not at all.
    /// </summary>
    /// <param name="tokenHash">
    /// SHA-256 of the token that is about to travel in the email. The caller mints the token,
    /// hashes it and keeps the clear value only long enough to build the URL — this aggregate
    /// never sees it, which makes "the token is not stored" a property of the type rather than a
    /// convention.
    /// </param>
    public Result AttachTokenAndMarkSent(byte[] tokenHash, TimeProvider clock)
    {
        // 32 bytes or nothing. A shorter digest means the caller hashed with something else, and a
        // link whose lookup silently never matches is indistinguishable from a customer who
        // ignored the email.
        if (tokenHash is not { Length: 32 })
            throw new DomainException("TokenHash must be a 32-byte SHA-256 digest.");

        // Only from Scheduled. The delayed send job is retried by Hangfire, and a second pass must
        // not mint a second token for one link — which would both mail the customer twice and
        // invalidate the link they already hold.
        if (Status != KycRemoteInvitationStatus.Scheduled)
            return Result.Fail(KycErrors.RemoteInvitationNotPending);

        TokenHash = tokenHash;
        Status = KycRemoteInvitationStatus.Sent;
        SentAt = clock.GetUtcNow();
        return Result.Ok();
    }

    /// <summary>
    /// Withdraws the link. Idempotent on an already-inactive one: an agent re-issuing a link while
    /// the nightly sweep expires it must not get an error about a link neither of them cares about
    /// any more.
    /// </summary>
    /// <param name="reason">
    /// Mandatory. A link disappearing from a customer's reach with no recorded motive is the kind
    /// of gap a support call cannot close.
    /// </param>
    public Result Revoke(string reason, Guid? revokedBy, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(KycErrors.RemoteInvitationRevokeReasonRequired);

        if (!IsActive)
            return Result.Ok();

        Status = KycRemoteInvitationStatus.Revoked;
        RevokedAt = clock.GetUtcNow();
        RevokedBy = revokedBy;
        RevokeReason = reason.Trim();
        return Result.Ok();
    }

    /// <summary>
    /// Closes a link whose window has passed. Answers false when there was nothing to close, so
    /// the sweep can count what it actually did rather than what it looked at.
    /// </summary>
    public bool Expire(TimeProvider clock)
    {
        var now = clock.GetUtcNow();

        if (!IsActive || now < ExpiresAt)
            return false;

        Status = KycRemoteInvitationStatus.Expired;
        return true;
    }
}
