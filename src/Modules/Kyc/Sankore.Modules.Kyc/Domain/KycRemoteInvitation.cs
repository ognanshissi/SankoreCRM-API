namespace Sankore.Modules.Kyc.Domain;

using System.Text.Json;
using Sankore.Modules.Kyc.Infrastructure.Storage;
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
/// <summary>
/// What <see cref="KycRemoteInvitation.VerifyOtp"/> concluded (KRM-B-12). Four outcomes rather
/// than a boolean because they call for four different instructions — and because the endpoint
/// deliberately shows the customer fewer than four, which is a choice that has to be visible at
/// the call site rather than buried in the aggregate.
/// </summary>
public enum KycOtpVerdict
{
    /// <summary>No code has been issued, or the pending one has already been used.</summary>
    NonePending,

    /// <summary>The ten-minute window has closed. A new code can be asked for.</summary>
    Expired,

    /// <summary>Three wrong tries against this code. A new code resets the count.</summary>
    TooManyAttempts,

    /// <summary>Wrong digits — or not six digits at all, which costs no attempt.</summary>
    Wrong,

    /// <summary>Correct, and consumed: the session may be issued.</summary>
    Accepted
}

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
    /// SHA-256 of the six-digit second factor, salted with this invitation's id (KRM-B-12). Null
    /// when the tenant does not require one, and null again once it has been consumed.
    ///
    /// <para>
    /// Hash-only for the reason <see cref="TokenHash"/> is: it is a credential, and a database
    /// dump, a replica or a backup must not contain a usable one.
    /// </para>
    /// </summary>
    public byte[]? OtpHash { get; private set; }

    /// <summary>When the code stops being accepted. Ten minutes after it was issued.</summary>
    public DateTimeOffset? OtpExpiresAt { get; private set; }

    /// <summary>
    /// Wrong codes typed against the CURRENT code. Reset by each new code, unlike
    /// <see cref="FailedAttempts"/>, which is per link: a customer who asks for a fresh code after
    /// mistyping twice has not spent two of the three tries on the new one.
    /// </summary>
    public int OtpAttempts { get; private set; }

    /// <summary>
    /// How many times the customer retook each photo, keyed by document kind (KRM-B-06). A
    /// document retaken past the tenant's budget is a signal for the validator rather than a
    /// refusal, so the count has to survive the submission.
    /// <para>Written from KRM-B-06 onwards; empty until then.</para>
    /// </summary>
    public string QualityRetriesJson { get; private set; } = "{}";

    /// <summary>
    /// On a <see cref="KycRemoteInvitationPurpose.Complement"/> link, the document kinds the agent
    /// asked the customer to RETAKE — a JSON array of <c>KycDocumentKind</c> names (KRM-B-10).
    /// Empty on an initial link, which collects everything the product requires.
    ///
    /// <para>
    /// <b>It narrows the journey, and it also re-opens it.</b> A complement link asks for exactly
    /// these kinds, and a photograph the customer already sent must no longer count for them —
    /// otherwise the journey would open with nothing missing and a send button that submits the
    /// same rejected image again. <see cref="KycRemoteJourney"/> therefore counts a listed kind as
    /// present only when it was uploaded after <see cref="CreatedAt"/>: the old evidence stays on
    /// the file, where it belongs, and stops satisfying this round.
    /// </para>
    ///
    /// <para>
    /// Kind NAMES and not a bitmask: the column is read in psql during a compliance question
    /// ("what was this customer asked for a second time"), and an integer there is a lookup in a
    /// source file.
    /// </para>
    /// </summary>
    public string RequestedKindsJson { get; private set; } = "[]";

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
        IReadOnlyCollection<KycDocumentKind>? requestedKinds = null,
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
            // Only meaningful on a complement. An initial link that carried a scope would be a
            // link that collects less than the product asks for, which no caller wants and which
            // the journey would have to second-guess.
            RequestedKindsJson = purpose == KycRemoteInvitationPurpose.Complement
                                 && requestedKinds is { Count: > 0 }
                ? JsonSerializer.Serialize(
                    requestedKinds.Distinct().Select(k => k.ToString()).ToList(), RetriesJson)
                : "[]",
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
    /// Records that somebody holding the link opened it. Answers whether anything moved.
    ///
    /// <para>
    /// <b>FIRST open only.</b> <see cref="OpenedAt"/> answers "did this message reach a human and
    /// when", which is a deliverability question; letting every page refresh overwrite it would
    /// turn it into "last touched" and lose the one piece of information an agent chasing a silent
    /// customer actually needs.
    /// </para>
    ///
    /// <para>
    /// The status only moves <c>Sent → Opened</c>. A link already <c>Identified</c> is further
    /// along, and walking it backwards on a refresh would make the journey appear to un-happen.
    /// </para>
    /// </summary>
    public bool MarkOpened(TimeProvider clock)
    {
        if (OpenedAt is not null && Status != KycRemoteInvitationStatus.Sent)
            return false;

        var moved = false;

        if (OpenedAt is null)
        {
            OpenedAt = clock.GetUtcNow();
            moved = true;
        }

        if (Status == KycRemoteInvitationStatus.Sent)
        {
            Status = KycRemoteInvitationStatus.Opened;
            moved = true;
        }

        return moved;
    }

    /// <summary>
    /// The customer answered the identification questions correctly: the link may now carry a
    /// session.
    ///
    /// <para>
    /// <b>The failed-attempt counter is reset.</b> A customer who mistyped their date of birth
    /// three times and then got it right has not used up three of their five tries for the rest of
    /// the link's life — the counter exists to stop a guessing run, and a correct answer ends the
    /// run. Keeping it would lock out the one person the link belongs to, on their next session,
    /// for mistakes they already recovered from.
    /// </para>
    ///
    /// <para>
    /// Re-identification is allowed while the link lives: <see cref="IdentifiedAt"/> keeps the
    /// FIRST success for the same reason <see cref="OpenedAt"/> keeps the first open, and a
    /// customer whose 20-minute session lapsed mid-upload comes back through this method rather
    /// than losing the photos they already sent.
    /// </para>
    /// </summary>
    public Result MarkIdentified(TimeProvider clock)
    {
        if (!IsUsableAt(clock.GetUtcNow()))
            return Result.Fail(KycErrors.RemoteInvitationNotUsable);

        IdentifiedAt ??= clock.GetUtcNow();
        Status = KycRemoteInvitationStatus.Identified;
        FailedAttempts = 0;
        return Result.Ok();
    }

    /// <summary>
    /// The customer answered wrongly. Answers whether that attempt was the one that locked the
    /// link.
    ///
    /// <para>
    /// This counter is the per-token rate limit, and a durable one: an in-memory window resets on
    /// every deployment and is per-instance, so it bounds nothing an attacker with patience cares
    /// about. The IP-based limiter in front of these endpoints bounds the FLOOD; this bounds the
    /// GUESSING, which against a date of birth is the attack that matters.
    /// </para>
    /// </summary>
    /// <summary>
    /// Attaches a freshly minted second factor (KRM-B-12).
    ///
    /// <para>
    /// Only on a link that is still usable: issuing a code against an expired or revoked
    /// invitation would create a credential for a journey nobody can continue, and the endpoint
    /// that calls this has already resolved the link through <c>KycRemoteGate</c>.
    /// </para>
    ///
    /// <para>
    /// A second call REPLACES the code and resets <see cref="OtpAttempts"/> — "send me another
    /// one" has to work, because a text message that does not arrive is the ordinary failure of
    /// this mechanism. The per-link <see cref="FailedAttempts"/> counter is deliberately NOT reset:
    /// that one bounds guessing at the client number and date of birth, and a new code must not
    /// buy an attacker five fresh tries at the first factor.
    /// </para>
    /// </summary>
    public Result IssueOtp(byte[] hash, TimeSpan validity, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (hash.Length == 0)
            throw new DomainException("An empty digest is not a code.");

        var now = clock.GetUtcNow();

        if (!IsUsableAt(now))
            return Result.Fail(KycErrors.RemoteInvitationNotUsable);

        OtpHash = hash;
        OtpExpiresAt = now.Add(validity);
        OtpAttempts = 0;
        return Result.Ok();
    }

    /// <summary>
    /// Checks a code and consumes it on success (KRM-B-12).
    ///
    /// <para>
    /// <b>Four outcomes, and each is a different thing to tell the customer.</b> No code pending
    /// (they have not asked for one, or it has already been used); the window has closed; too many
    /// wrong tries; or wrong. The endpoint collapses the last two into one message on purpose —
    /// see its own remarks — but the aggregate distinguishes them, because "ask for a new code"
    /// and "wait" are different instructions and only the caller knows which it may safely give.
    /// </para>
    ///
    /// <para>
    /// <b>The code is cleared on success.</b> It is a one-time factor: a second call with the same
    /// digits must not open a second session, and leaving it in place would make the column a
    /// replayable credential for ten minutes.
    /// </para>
    /// </summary>
    /// <param name="candidateHash">
    /// The digest of what the customer typed, or null when it was not even code-shaped. Null is a
    /// FAILURE that costs no attempt: a blank field or a four-digit entry is a typing accident, and
    /// charging it against three tries would let a bored script burn somebody else's factor.
    /// </param>
    public KycOtpVerdict VerifyOtp(byte[]? candidateHash, int maxAttempts, TimeProvider clock)
    {
        if (OtpHash is null || OtpExpiresAt is null) return KycOtpVerdict.NonePending;

        if (clock.GetUtcNow() >= OtpExpiresAt.Value) return KycOtpVerdict.Expired;

        if (maxAttempts > 0 && OtpAttempts >= maxAttempts) return KycOtpVerdict.TooManyAttempts;

        if (candidateHash is null) return KycOtpVerdict.Wrong;

        // Fixed-time, like every other credential comparison in this journey: the digests are the
        // same length, so what must not leak is where they first differ.
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                candidateHash, OtpHash))
        {
            OtpAttempts++;
            return KycOtpVerdict.Wrong;
        }

        // Consumed. One factor, one session.
        OtpHash = null;
        OtpExpiresAt = null;
        OtpAttempts = 0;
        return KycOtpVerdict.Accepted;
    }

    /// <summary>True while a code is pending and its window is still open.</summary>
    public bool HasPendingOtpAt(DateTimeOffset now)
        => OtpHash is not null && OtpExpiresAt is { } expiry && now < expiry;

    public bool RecordFailedIdentification(int maxAttempts, TimeProvider clock)
    {
        if (maxAttempts < 1)
            throw new DomainException("A link must allow at least one identification attempt.");

        // An inactive link cannot be locked further, and counting against it would let a stale
        // token's failures look like activity on the file.
        if (!IsActive) return false;

        FailedAttempts++;

        if (FailedAttempts < maxAttempts)
            return false;

        Status = KycRemoteInvitationStatus.Locked;
        LockedAt = clock.GetUtcNow();
        return true;
    }

    /// <summary>
    /// The customer sent their file. The link is spent from here on, and so is the session built
    /// on it — <c>KycRemoteGate</c> re-reads this status on every call, which is what makes a
    /// signed session stop working without a revocation list.
    /// </summary>
    public Result MarkSubmitted(TimeProvider clock)
    {
        // Only from Identified: a submission with no identification behind it would be an upload
        // nobody proved they were entitled to make.
        if (Status != KycRemoteInvitationStatus.Identified)
            return Result.Fail(KycErrors.RemoteInvitationNotUsable);

        Status = KycRemoteInvitationStatus.Submitted;
        SubmittedAt = clock.GetUtcNow();
        return Result.Ok();
    }

    /// <summary>
    /// Records that the customer retook a photo of <paramref name="kind"/>, and answers how many
    /// retakes that kind has now had.
    ///
    /// <para>
    /// Counted per kind rather than for the link as a whole: a customer fighting the glare on a
    /// laminated card has not used up the budget for their selfie. The count survives the
    /// submission because it is what tells the validator that this document was hard to capture —
    /// which is a fact about the evidence, not an incident.
    /// </para>
    /// </summary>
    public int RecordQualityRetry(KycDocumentKind kind, TimeProvider clock)
    {
        var retries = QualityRetries();
        var next = retries.GetValueOrDefault(kind.ToString()) + 1;
        retries[kind.ToString()] = next;

        QualityRetriesJson = JsonSerializer.Serialize(retries, RetriesJson);
        return next;
    }

    /// <summary>Retakes so far, by <c>KycDocumentKind</c> name. Empty rather than null.</summary>
    public Dictionary<string, int> QualityRetries()
    {
        if (string.IsNullOrWhiteSpace(QualityRetriesJson))
            return new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(QualityRetriesJson, RetriesJson)
                   ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A hand-edited column must not break a customer's upload. The budget restarts, which
            // is the forgiving direction — the alternative is a journey that cannot continue.
            return new Dictionary<string, int>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The kinds this complement link asks for, by <c>KycDocumentKind</c> name. Empty on an
    /// initial link and on a complement that named none — both mean "everything the product
    /// requires", which is the reading a caller can act on without a null check.
    /// </summary>
    public IReadOnlyList<string> RequestedKinds()
    {
        if (string.IsNullOrWhiteSpace(RequestedKindsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(RequestedKindsJson, RetriesJson) ?? [];
        }
        catch (JsonException)
        {
            // A hand-edited column must not strand a customer mid-journey. An empty scope asks for
            // everything the product requires, which is the forgiving direction — the alternative
            // is a complement link that asks for nothing and cannot be submitted.
            return [];
        }
    }

    /// <summary>
    /// The customer accepted the processing of their biometric data, against a named version of
    /// the text they were shown.
    ///
    /// <para>
    /// Both, and the version is not optional: consent to a text nobody can identify afterwards is
    /// not evidence of anything. Recorded ONCE — a second acceptance does not move the date,
    /// because what has to be provable is when the customer agreed, not when they last reloaded
    /// the page.
    /// </para>
    /// </summary>
    public Result RecordConsent(string textVersion, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(textVersion))
            return Result.Fail(KycErrors.RemoteConsentVersionRequired);

        if (ConsentGivenAt is not null)
            return Result.Ok();

        ConsentGivenAt = clock.GetUtcNow();
        ConsentTextVersion = textVersion.Trim();
        return Result.Ok();
    }

    public bool HasConsented => ConsentGivenAt is not null;

    private static readonly JsonSerializerOptions RetriesJson = new(JsonSerializerDefaults.Web);

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
