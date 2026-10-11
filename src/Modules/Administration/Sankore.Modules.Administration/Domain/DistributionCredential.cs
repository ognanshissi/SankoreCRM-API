namespace Sankore.Modules.Administration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// What the CIMA code calls a <i>carte professionnelle</i>: the licence that allows a named person
/// to present insurance operations at an institution's counters (articles 503 and 510), together
/// with the professional indemnity cover and the training the code requires.
///
/// <para>
/// <b>Why a table and not columns on <c>AppUser</c>.</b> A card expires and is renewed, and the
/// question a controller asks is not "is this agent licensed today" but "was this agent licensed
/// on the day they presented this contract". Columns overwrite that history; this is historised
/// the way <c>BeneficialOwner</c> is — a change CLOSES the previous row instead of editing it, so
/// the register can be rebuilt as it stood on any past date.
/// </para>
///
/// <para>
/// <b>The number is encrypted.</b> A licence number identifies a named person and appears on a
/// regulator's register. It is held as ciphertext and matched through
/// <see cref="CardNumberBlindIndex"/>, never read back for display — the holder's name and the
/// card's validity are what a screen needs.
/// </para>
///
/// <para>
/// <b>Micro-insurance.</b> Book VII sets a 48-hour training requirement for the card of an
/// intermediary distributing micro-insurance (article 732), which is why
/// <see cref="TrainingHours"/> and <see cref="TrainingCompletedOn"/> are part of the credential
/// rather than of a separate HR record: they condition the licence, not the employment.
/// </para>
/// </summary>
public sealed class DistributionCredential : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>The user this licence belongs to.</summary>
    public Guid UserId { get; private set; }

    public DistributionCredentialCategory Category { get; private set; }

    /// <summary>AES-256-GCM. Never returned by a read endpoint.</summary>
    public string EncryptedCardNumber { get; private set; } = default!;

    /// <summary>
    /// HMAC-SHA256 of the normalised number — the only searchable half, so a duplicate card can
    /// be found without decrypting anything.
    /// </summary>
    public string CardNumberBlindIndex { get; private set; } = default!;

    /// <summary>
    /// The national insurance directorate that issued it. In clear: it is an institution, not a
    /// person.
    /// </summary>
    public string IssuingAuthority { get; private set; } = default!;

    public DateOnly IssuedOn { get; private set; }

    /// <summary>
    /// When the card stops being valid. Required: the code grants a licence for a term, and a
    /// credential with no end date would read as permanent to every guard that consults it.
    /// </summary>
    public DateOnly ExpiresOn { get; private set; }

    // ── Professional indemnity (CIMA Book V) ────────────────────────────────

    /// <summary>Insurer carrying the holder's professional indemnity cover.</summary>
    public string? IndemnityInsurer { get; private set; }

    public string? IndemnityPolicyReference { get; private set; }

    /// <summary>
    /// Expiry of the indemnity cover, which runs on its own term — a card can be valid while the
    /// cover behind it has lapsed, and that is exactly the state an institution must be able to
    /// see.
    /// </summary>
    public DateOnly? IndemnityExpiresOn { get; private set; }

    // ── Training (CIMA Book VII, article 732) ───────────────────────────────

    public int? TrainingHours { get; private set; }

    public DateOnly? TrainingCompletedOn { get; private set; }

    // ── Validity of the ROW, which is not the validity of the card ──────────

    /// <summary>
    /// When this row became the holder's current credential. Distinct from
    /// <see cref="IssuedOn"/>: the card was issued by an authority on one date and recorded here
    /// on another.
    /// </summary>
    public DateTimeOffset ValidFrom { get; private set; }

    /// <summary>
    /// When this row was superseded or withdrawn. <c>null</c> for the current one.
    /// </summary>
    public DateTimeOffset? ValidTo { get; private set; }

    /// <summary>
    /// Why the row was closed — a renewal, a suspension by the authority, a departure. Required
    /// when closing: "the card is gone" and "the card was suspended" are not the same fact to a
    /// controller.
    /// </summary>
    public string? ClosedReason { get; private set; }

    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? ClosedBy { get; private set; }

    /// <summary>PostgreSQL xmin, the concurrency convention of this solution.</summary>
    public uint Version { get; private set; }

    /// <summary>True while this is the holder's recorded credential.</summary>
    public bool IsCurrent => ValidTo is null;

    private DistributionCredential() { } // EF Core

    public static DistributionCredential Issue(
        Guid tenantId,
        Guid userId,
        DistributionCredentialCategory category,
        string encryptedCardNumber,
        string cardNumberBlindIndex,
        string issuingAuthority,
        DateOnly issuedOn,
        DateOnly expiresOn,
        Guid createdBy,
        DateTimeOffset now,
        string? indemnityInsurer = null,
        string? indemnityPolicyReference = null,
        DateOnly? indemnityExpiresOn = null,
        int? trainingHours = null,
        DateOnly? trainingCompletedOn = null)
    {
        if (tenantId == Guid.Empty)
            throw new DomainException("TenantId is required.", "Credential.Tenant.Required");
        if (userId == Guid.Empty)
            throw new DomainException("A credential belongs to a user.", "Credential.User.Required");
        if (string.IsNullOrWhiteSpace(encryptedCardNumber))
            throw new DomainException("The card number is required.", "Credential.CardNumber.Required");
        if (string.IsNullOrWhiteSpace(cardNumberBlindIndex))
            throw new DomainException(
                "The card number index is required.", "Credential.CardNumberIndex.Required");
        if (string.IsNullOrWhiteSpace(issuingAuthority))
            throw new DomainException(
                "The issuing authority is required.", "Credential.Authority.Required");

        // An expiry before the issue date is a typo whose consequence is a credential that was
        // never valid for a day, silently excluding its holder from every insurance dispatch.
        if (expiresOn <= issuedOn)
            throw new DomainException(
                "A credential must expire after it was issued.", "Credential.Term.Invalid");

        if (trainingHours is < 0)
            throw new DomainException(
                "Training hours cannot be negative.", "Credential.TrainingHours.Invalid");

        return new DistributionCredential
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            Category = category,
            EncryptedCardNumber = encryptedCardNumber,
            CardNumberBlindIndex = cardNumberBlindIndex,
            IssuingAuthority = issuingAuthority.Trim(),
            IssuedOn = issuedOn,
            ExpiresOn = expiresOn,
            IndemnityInsurer = Blank(indemnityInsurer),
            IndemnityPolicyReference = Blank(indemnityPolicyReference),
            IndemnityExpiresOn = indemnityExpiresOn,
            TrainingHours = trainingHours,
            TrainingCompletedOn = trainingCompletedOn,
            ValidFrom = now,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Closes this row. Called when a renewal supersedes it, when the authority suspends it, or
    /// when the holder stops distributing.
    /// </summary>
    public Result Close(string reason, Guid actor, DateTimeOffset now)
    {
        if (ValidTo is not null)
            return Result.Fail(CredentialErrorCodes.AlreadyClosed);

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Fail(CredentialErrorCodes.CloseReasonRequired);

        ValidTo = now;
        ClosedReason = reason.Trim();
        ClosedBy = actor;
        return Result.Ok();
    }

    /// <summary>
    /// Whether the holder may present insurance operations <paramref name="asOf"/>: the row is
    /// current, the card's term covers that date, and the indemnity cover — when one is recorded
    /// — has not lapsed.
    ///
    /// <para>
    /// <b>The single definition</b>, so the dispatching filter, the subscription gate and the
    /// expiry report cannot grow three versions of it. The lesson
    /// <c>PepStatus.IsExposedAt</c> already encodes: a date window compared by hand in three
    /// places becomes three windows.
    /// </para>
    ///
    /// <para>
    /// A recorded indemnity cover that has lapsed INVALIDATES the credential, while no recorded
    /// cover does not. That asymmetry is deliberate: the code requires the cover, but this table
    /// is also the place a card gets recorded before its indemnity paperwork arrives, and
    /// refusing a card for a field nobody has filled yet would push institutions into recording
    /// no cards at all. An absent cover is visible in <see cref="HasIndemnityCover"/>, which the
    /// report exposes.
    /// </para>
    /// </summary>
    public bool IsValidAt(DateOnly asOf)
        => IsCurrent
        && asOf >= IssuedOn
        && asOf <= ExpiresOn
        && (IndemnityExpiresOn is null || asOf <= IndemnityExpiresOn);

    /// <summary>Whether an indemnity cover is recorded at all — not whether it is current.</summary>
    public bool HasIndemnityCover => IndemnityExpiresOn is not null;

    /// <summary>
    /// Whether the holder meets the micro-insurance training requirement of article 732.
    /// <paramref name="requiredHours"/> is a tenant parameter so the figure is not frozen in code
    /// against a reforming text.
    /// </summary>
    public bool MeetsTrainingRequirement(int requiredHours)
        => TrainingHours is { } hours && hours >= requiredHours && TrainingCompletedOn is not null;

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The capacities article 503 and Book VII recognise. Stored as a string, like every other enum
/// in this solution's schemas.
/// </summary>
public enum DistributionCredentialCategory
{
    /// <summary>Agent général d'assurance.</summary>
    GeneralAgent,

    /// <summary>Courtier.</summary>
    Broker,

    /// <summary>Mandataire d'intermédiaire.</summary>
    Proxy,

    /// <summary>
    /// The capacity of an employee presenting operations at the counters of a bank, a microfinance
    /// institution, a savings bank or the post office — the route article 503 opens to an IMF, and
    /// the one most of this platform's users hold.
    /// </summary>
    CounterPresenter,
}

internal static class CredentialErrorCodes
{
    public const string AlreadyClosed = "CREDENTIAL_ALREADY_CLOSED";
    public const string CloseReasonRequired = "CREDENTIAL_CLOSE_REASON_REQUIRED";
    public const string NotFound = "CREDENTIAL_NOT_FOUND";
    public const string UserNotFound = "CREDENTIAL_USER_NOT_FOUND";
    public const string DuplicateCardNumber = "CREDENTIAL_CARD_NUMBER_ALREADY_RECORDED";

    /// <summary>
    /// The holder already has a current credential. A renewal closes it first — an endpoint that
    /// simply added a second row would leave two cards current and every guard reading whichever
    /// came back first.
    /// </summary>
    public const string AlreadyHasCurrent = "CREDENTIAL_ALREADY_CURRENT";
}
