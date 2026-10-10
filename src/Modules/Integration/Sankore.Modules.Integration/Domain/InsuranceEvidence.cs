namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// The customer's consent and signature, kept as evidence (ASS-04, last-but-one criterion;
/// ASS-12, criterion 2 — règlement CIMA 2024 sur l'assurance numérique).
///
/// <para>
/// <b>Its own table, not a column on the subscription</b>, for three reasons that each decide it
/// on their own:
/// </para>
///
/// <list type="number">
/// <item><b>Retention.</b> ASS-12 requires the proof to be kept « pendant la durée exigée (à
///   confirmer) » — a period nobody has fixed yet, and certainly longer than the operational life
///   of the saga row. <see cref="RetainUntil"/> is nullable and <c>null</c> means <i>no purge date
///   has been decided</i>: a purge job must refuse to sweep such a row rather than treat the
///   absence of a date as permission. Putting the proof on a row that an operational purge will
///   one day touch is the mistake this separation prevents.</item>
/// <item><b>Immutability.</b> The subscription mutates on every step of the chain. Evidence does
///   not change, ever — which is why this entity has <b>no xmin token</b>: there is no second
///   writer to lose a race with, and a concurrency column on an append-only row would state
///   something untrue about it.</item>
/// <item><b>Attachment.</b> ASS-04 says the proof is « rattachée à la commande ». It carries both
///   <see cref="SubscriptionId"/> and <see cref="CommandId"/>, so it is reachable from the
///   business object and from the integration command the criterion names.</item>
/// </list>
///
/// <para>
/// The evidence itself is never in clear. Either <see cref="EvidenceEncrypted"/> holds it inline
/// (a one-time code transcript, a few hundred bytes) or <see cref="EvidenceStorageRef"/> points at
/// an object in the module's encrypted file store (a signature image, a scanned mandate) — field
/// level or file level, the two branches ASS-12 allows. <see cref="EvidenceSha256"/> is over the
/// PLAINTEXT, so the proof can be shown to be the one that was captured.
/// </para>
/// </summary>
public sealed class ConsentProof : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>The subscription this proves. Intra-schema FK, and unique: one proof, one contract.</summary>
    public Guid SubscriptionId { get; private set; }

    /// <summary>
    /// The <c>SubscribePolicy</c> command the criterion attaches it to. A plain Guid, no FK: a
    /// command may be purged by a retention job decades before this proof may be.
    /// </summary>
    public Guid? CommandId { get; private set; }

    /// <summary>Opaque reference to M01's customer. Denormalised so a compliance search needs no join.</summary>
    public Guid CrmCustomerId { get; private set; }

    public ConsentChannel Channel { get; private set; }

    /// <summary>
    /// Which version of the pre-contractual notice the customer accepted. Required: a consent to
    /// an unidentified text proves nothing, and ASS-12's last criterion makes the wording itself
    /// — SANKORE's role as the insurer's technical provider — part of what must be shown.
    /// </summary>
    public string ConsentTextVersion { get; private set; } = string.Empty;

    /// <summary>A stable digest of the notice text actually displayed, when the slice captures it.</summary>
    public string? ConsentTextSha256 { get; private set; }

    public DateTimeOffset ConsentedAt { get; private set; }

    /// <summary>Encrypted evidence held inline. <c>text</c>, no cap — see the class remarks.</summary>
    public string? EvidenceEncrypted { get; private set; }

    /// <summary>Reference of the encrypted object, when the evidence is a file.</summary>
    public string? EvidenceStorageRef { get; private set; }

    /// <summary>SHA-256 of the PLAINTEXT evidence, lower-case hex.</summary>
    public string? EvidenceSha256 { get; private set; }

    public string? ContentType { get; private set; }

    /// <summary>The agent who collected it, and when the row was written.</summary>
    public Guid CapturedBy { get; private set; }

    public DateTimeOffset CapturedAt { get; private set; }

    /// <summary>
    /// The date after which a purge may remove this row. <c>null</c> means the retention period
    /// has not been decided — <b>not</b> "keep forever" and emphatically not "sweepable now". Any
    /// job deleting from this table must skip a null, and say why in its own code.
    /// </summary>
    public DateTimeOffset? RetainUntil { get; private set; }

    private ConsentProof() { }

    public static ConsentProof Create(
        Guid tenantId,
        Guid subscriptionId,
        Guid crmCustomerId,
        ConsentChannel channel,
        string consentTextVersion,
        DateTimeOffset consentedAt,
        Guid capturedBy,
        TimeProvider clock,
        Guid? commandId = null,
        string? consentTextSha256 = null,
        string? evidenceEncrypted = null,
        string? evidenceStorageRef = null,
        string? evidenceSha256 = null,
        string? contentType = null,
        DateTimeOffset? retainUntil = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (subscriptionId == Guid.Empty) throw new DomainException("SubscriptionId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");
        if (string.IsNullOrWhiteSpace(consentTextVersion))
            throw new DomainException(
                "A consent proof must name the version of the notice the customer accepted; "
                + "without it the proof does not say what was consented to.");

        // One or the other, never neither: a proof that holds no evidence is a row claiming
        // consent was collected with nothing to show a controller.
        if (string.IsNullOrWhiteSpace(evidenceEncrypted) && string.IsNullOrWhiteSpace(evidenceStorageRef))
            throw new DomainException(
                "A consent proof needs its evidence: either encrypted inline, or a reference to "
                + "the encrypted object that holds it.");

        ArgumentNullException.ThrowIfNull(clock);

        return new ConsentProof
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            CommandId = commandId == Guid.Empty ? null : commandId,
            CrmCustomerId = crmCustomerId,
            Channel = channel,
            ConsentTextVersion = consentTextVersion.Trim(),
            ConsentTextSha256 = consentTextSha256?.Trim(),
            ConsentedAt = consentedAt,
            EvidenceEncrypted = evidenceEncrypted,
            EvidenceStorageRef = evidenceStorageRef?.Trim(),
            EvidenceSha256 = evidenceSha256?.Trim(),
            ContentType = contentType?.Trim(),
            CapturedBy = capturedBy,
            CapturedAt = clock.GetUtcNow(),
            RetainUntil = retainUntil,
        };
    }

    /// <summary>
    /// Links the proof to the command once the chain has created it. The <b>only</b> mutation this
    /// entity allows, and it adds a reference rather than changing evidence — which is why it
    /// refuses to overwrite an existing link.
    /// </summary>
    public void AttachCommand(Guid commandId)
    {
        if (commandId == Guid.Empty) throw new DomainException("CommandId is required.");

        if (CommandId is not null && CommandId != commandId)
            throw new DomainException(
                "A consent proof is already attached to another command; re-pointing it would "
                + "move evidence between contracts.");

        CommandId = commandId;
    }
}

/// <summary>
/// The medical questionnaire of one subscription (ASS-04, criterion 2; ASS-12, criterion 1).
///
/// <para>
/// <b>Its own table and not a column, because of the permission.</b> ASS-12's third criterion
/// requires access to medical data to be restricted by a <i>dedicated</i> permission and traced.
/// A column on <c>ins_subscription</c> would be read by every projection of a subscription — the
/// counter screen, the statement generator, the saga's own event consumers — and "nobody selected
/// that column today" is not a restriction anybody can audit. A separate table makes the
/// restriction structural: exactly one query path touches it, and that path is the one the
/// permission and the trace sit on.
/// </para>
///
/// <para>
/// The answers are a single AES-256-GCM value under this module's key, in a <c>text</c> column
/// with no cap. They are <b>never</b> projected into <c>answers_summary</c>-style columns, never
/// put in an <c>IntegrationCommand</c> payload field-name list beyond the collection's own name
/// (see <c>CommandPayloadProtector</c>, which lists top-level names only for exactly this
/// reason), and never logged.
/// </para>
/// </summary>
public sealed class MedicalQuestionnaire : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Intra-schema FK, unique: one questionnaire per subscription.</summary>
    public Guid SubscriptionId { get; private set; }

    /// <summary>Opaque reference to M01's customer.</summary>
    public Guid CrmCustomerId { get; private set; }

    /// <summary>
    /// Which questionnaire the insurer asked — its form code. In clear: it names a form, not a
    /// person's health, and the insurer needs it to read the answers back.
    /// </summary>
    public string QuestionnaireCode { get; private set; } = string.Empty;

    /// <summary>The answers, encrypted. The whole reason this table is separate.</summary>
    public string AnswersEncrypted { get; private set; } = string.Empty;

    /// <summary>
    /// Whether the answers flagged a condition that requires the insurer's medical underwriting.
    /// A single boolean in clear, deliberately: the subscription screen has to know that an
    /// underwriting delay applies, and telling it that costs strictly less than letting it read
    /// the answers to work it out.
    /// </summary>
    public bool RequiresMedicalUnderwriting { get; private set; }

    public DateTimeOffset CompletedAt { get; private set; }

    public Guid CapturedBy { get; private set; }

    /// <summary>Same rule as <see cref="ConsentProof.RetainUntil"/>: null is not permission to purge.</summary>
    public DateTimeOffset? RetainUntil { get; private set; }

    private MedicalQuestionnaire() { }

    public static MedicalQuestionnaire Create(
        Guid tenantId,
        Guid subscriptionId,
        Guid crmCustomerId,
        string questionnaireCode,
        string answersEncrypted,
        Guid capturedBy,
        TimeProvider clock,
        bool requiresMedicalUnderwriting = false,
        DateTimeOffset? retainUntil = null,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (subscriptionId == Guid.Empty) throw new DomainException("SubscriptionId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");
        if (string.IsNullOrWhiteSpace(questionnaireCode))
            throw new DomainException("A questionnaire code is required.");

        // The guard that matters most in this file: an empty value would be a row asserting a
        // questionnaire was collected while holding nothing, and the underwriting flag beside it
        // would then be the only thing an underwriter sees.
        if (string.IsNullOrWhiteSpace(answersEncrypted))
            throw new DomainException("A medical questionnaire with no answers must not be stored.");

        ArgumentNullException.ThrowIfNull(clock);

        return new MedicalQuestionnaire
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            CrmCustomerId = crmCustomerId,
            QuestionnaireCode = questionnaireCode.Trim(),
            AnswersEncrypted = answersEncrypted,
            RequiresMedicalUnderwriting = requiresMedicalUnderwriting,
            CompletedAt = clock.GetUtcNow(),
            CapturedBy = capturedBy,
            RetainUntil = retainUntil,
        };
    }
}
