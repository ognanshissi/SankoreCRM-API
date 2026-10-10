namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One person's FATCA self-certification (BNF-B-11) — the declared indices, the documentation
/// provided, and the status those two produce.
///
/// <para>
/// <b>One ACTIVE declaration per person</b>, held by
/// <c>ux_fatca_declarations_active_per_subject</c>. A change of circumstances does not edit the
/// existing row: it supersedes it (<see cref="SupersededAt"/>) and a new one takes over, so the
/// succession of what somebody declared and when stays readable. A regulator asking "what did this
/// person certify in March" has to get March's answer.
/// </para>
/// </summary>
public sealed class FatcaDeclaration : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }

    /// <summary>
    /// Whose declaration it is. <see cref="QualificationSubjectType.Principal"/> carries the
    /// lead's own id — the principal has no id of its own, it IS the lead — and a co-holder is a
    /// <see cref="QualificationSubjectType.HouseholdMember"/>.
    /// </summary>
    public QualificationSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    /// <summary>The seven indices as declared. Typed jsonb — see <see cref="FatcaIndicia"/>.</summary>
    public FatcaIndicia Indicia { get; private set; } = FatcaIndicia.None;

    public FatcaDocumentation Documentation { get; private set; }

    /// <summary>
    /// The person's US taxpayer identification number. **Encrypted at rest** and required when
    /// <see cref="Documentation"/> is <see cref="FatcaDocumentation.FormW9"/> (BNF-B-11).
    /// </summary>
    public string? UsTin { get; private set; }

    /// <summary>
    /// Object-storage reference to the signed self-certification (F02.10). A REFERENCE, never the
    /// bytes: a scanned form in a jsonb or bytea column is a row nobody can stream and a backup
    /// nobody can size.
    /// </summary>
    public string? SelfCertificationDocRef { get; private set; }

    /// <summary>
    /// Object-storage reference to the evidence supporting a W-8BEN — a non-US passport, a
    /// certificate of loss of nationality, or a written explanation, depending on which index is
    /// being cured (BNF-B-11). Required for <see cref="FatcaDocumentation.FormW8Ben"/>.
    /// </summary>
    public string? EvidenceDocRef { get; private set; }

    /// <summary>When the person signed the self-certification. Not when the row was written.</summary>
    public DateTimeOffset? SignedAt { get; private set; }

    /// <summary>Why the person refused, for a <see cref="FatcaStatus.Recalcitrant"/> declaration.</summary>
    public string? RefusalReason { get; private set; }

    /// <summary>
    /// The status, **computed from the declaration's own content and stored**.
    ///
    /// <para>
    /// Stored because the compliance screen filters on it and because
    /// <c>FatcaStatusChanged</c> has to fire at the moment it moves. Computed because BNF-B-11
    /// says so: nobody picks a FATCA status from a dropdown — it follows from what was declared and
    /// what was produced, and a settable field would let an operator record "NonUsPerson" over a
    /// declared US birthplace.
    /// </para>
    ///
    /// <para>
    /// Never <see cref="FatcaStatus.NotApplicable"/>: that is a property of the TENANT, not of the
    /// declaration — see <see cref="EffectiveStatus"/>.
    /// </para>
    /// </summary>
    public FatcaStatus Status { get; private set; }

    public Guid RecordedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Null while this is the person's current declaration.</summary>
    public DateTimeOffset? SupersededAt { get; private set; }

    /// <summary>The declaration that replaced this one. An opaque id within the same table.</summary>
    public Guid? SupersededByDeclarationId { get; private set; }

    public bool IsActive => SupersededAt is null;

    private FatcaDeclaration() { } // EF Core

    /// <summary>
    /// Records a declaration.
    /// </summary>
    /// <param name="usTin">
    /// In CLEAR — the mapping encrypts it. Required for <see cref="FatcaDocumentation.FormW9"/>.
    /// </param>
    /// <returns>
    /// <c>FATCA_TIN_REQUIRED</c>, <c>FATCA_EVIDENCE_REQUIRED</c>,
    /// <c>FATCA_SELF_CERTIFICATION_REQUIRED</c>, <c>FATCA_REFUSAL_REASON_REQUIRED</c> or
    /// <c>FATCA_SIGNATURE_REQUIRED</c>.
    /// </returns>
    public static Result<FatcaDeclaration> Record(
        Guid tenantId,
        Guid leadId,
        QualificationSubjectType subjectType,
        Guid subjectId,
        FatcaIndicia indicia,
        FatcaDocumentation documentation,
        Guid recordedBy,
        DateTimeOffset now,
        string? usTin = null,
        string? selfCertificationDocRef = null,
        string? evidenceDocRef = null,
        DateTimeOffset? signedAt = null,
        string? refusalReason = null)
    {
        ArgumentNullException.ThrowIfNull(indicia);

        switch (documentation)
        {
            case FatcaDocumentation.FormW9 when string.IsNullOrWhiteSpace(usTin):
                // A W-9 without a TIN is the form's whole purpose missing. The person is reportable
                // and there is nothing to report them under.
                return Result.Fail<FatcaDeclaration>("FATCA_TIN_REQUIRED");

            case FatcaDocumentation.FormW8Ben when string.IsNullOrWhiteSpace(evidenceDocRef):
                // BNF-B-11: "W-8BEN plus pièce justificative". The form alone asserts non-US
                // status; the evidence is what lifts the index that contradicted it.
                return Result.Fail<FatcaDeclaration>("FATCA_EVIDENCE_REQUIRED");

            case FatcaDocumentation.Refused when string.IsNullOrWhiteSpace(refusalReason):
                return Result.Fail<FatcaDeclaration>("FATCA_REFUSAL_REASON_REQUIRED");
        }

        // The self-certification is the artefact (F02.10) — required for every declaration that is
        // not a refusal, including a plain "no indices": that negative statement is exactly what an
        // institution has to be able to produce.
        if (documentation != FatcaDocumentation.Refused)
        {
            if (string.IsNullOrWhiteSpace(selfCertificationDocRef))
                return Result.Fail<FatcaDeclaration>("FATCA_SELF_CERTIFICATION_REQUIRED");
            if (signedAt is null)
                return Result.Fail<FatcaDeclaration>("FATCA_SIGNATURE_REQUIRED");
            if (signedAt > now)
                return Result.Fail<FatcaDeclaration>("FATCA_SIGNATURE_IN_FUTURE");
        }

        var declaration = new FatcaDeclaration
        {
            Id                      = Guid.NewGuid(),
            TenantId                = tenantId,
            LeadId                  = leadId,
            SubjectType             = subjectType,
            SubjectId               = subjectId,
            Indicia                 = indicia,
            Documentation           = documentation,
            UsTin                   = Blank(usTin),
            SelfCertificationDocRef = Blank(selfCertificationDocRef),
            EvidenceDocRef          = Blank(evidenceDocRef),
            SignedAt                = signedAt,
            RefusalReason           = Blank(refusalReason),
            RecordedBy              = recordedBy,
            CreatedAt               = now,
        };

        declaration.Status = ComputeStatus(indicia, documentation);
        return Result.Ok(declaration);
    }

    /// <summary>
    /// The whole of BNF-B-11's status rule, in evaluation order. The ORDER is the logic:
    ///
    /// <list type="number">
    /// <item>A refusal is <see cref="FatcaStatus.Recalcitrant"/> whatever else is true.</item>
    /// <item>
    /// A W-9 is <see cref="FatcaStatus.UsPerson"/> even with no index declared. An index is an
    /// INDICATOR; a W-9 is the person stating the fact, and a statement beats the absence of a
    /// hint.
    /// </item>
    /// <item>
    /// No index at all is <see cref="FatcaStatus.NonUsPerson"/> — including when a W-8BEN was
    /// produced anyway, because <see cref="FatcaStatus.IndiciaCured"/> would assert that indices
    /// were lifted which never existed.
    /// </item>
    /// <item>A W-8BEN against declared indices is <see cref="FatcaStatus.IndiciaCured"/>.</item>
    /// <item>Anything else — indices declared, nothing produced — is <see cref="FatcaStatus.IndiciaPending"/>.</item>
    /// </list>
    /// </summary>
    internal static FatcaStatus ComputeStatus(FatcaIndicia indicia, FatcaDocumentation documentation)
        => documentation switch
        {
            FatcaDocumentation.Refused => FatcaStatus.Recalcitrant,
            FatcaDocumentation.FormW9  => FatcaStatus.UsPerson,
            _ when !indicia.Any        => FatcaStatus.NonUsPerson,
            FatcaDocumentation.FormW8Ben => FatcaStatus.IndiciaCured,
            _                          => FatcaStatus.IndiciaPending,
        };

    /// <summary>
    /// The status a reader should act on, given whether the tenant is subject to FATCA.
    ///
    /// <para>
    /// <see cref="FatcaStatus.NotApplicable"/> is never stored, because turning FATCA off for a
    /// tenant would otherwise mean rewriting every declaration — and turning it back on would mean
    /// recomputing them from data that has not changed. The toggle is a read-side concern, and the
    /// declarations collected while it was on are retained rather than reinterpreted.
    /// </para>
    /// </summary>
    public FatcaStatus EffectiveStatus(bool fatcaEnabled)
        => fatcaEnabled ? Status : FatcaStatus.NotApplicable;

    /// <summary>
    /// Whether this declaration still owes documentation — what a completeness check asks.
    /// </summary>
    public bool IsDocumentationOutstanding => Status == FatcaStatus.IndiciaPending;

    /// <summary>
    /// Whether this status is one the compliance officer must be told about (BNF-B-11: "UsPerson et
    /// Recalcitrant signalés au responsable conformité").
    /// </summary>
    public bool RequiresComplianceReview
        => Status is FatcaStatus.UsPerson or FatcaStatus.Recalcitrant;

    /// <summary>
    /// Marks this declaration as replaced by <paramref name="replacementId"/> (BNF-B-11: "changement
    /// de circonstances : nouvelle déclaration, l'ancienne est historisée").
    /// </summary>
    internal Result Supersede(Guid replacementId, DateTimeOffset now)
    {
        if (!IsActive)
            return Result.Fail("FATCA_DECLARATION_ALREADY_SUPERSEDED");

        SupersededAt              = now;
        SupersededByDeclarationId = replacementId;
        return Result.Ok();
    }

    /// <summary>
    /// Blanks the identifying content, keeping the row (BNF-B-19). The status and the dates survive:
    /// they are what says a certification happened, which is the part an anonymisation must not
    /// destroy.
    /// </summary>
    internal void Anonymize()
    {
        UsTin                   = null;
        SelfCertificationDocRef = null;
        EvidenceDocRef          = null;
        RefusalReason           = null;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
