namespace Sankore.Modules.Leads.Tests.Features.Fatca;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Covers the computed FATCA status (BNF-B-11) — the one piece of this story that is pure logic,
/// and the piece a dropdown must never be able to override.
/// </summary>
public sealed class FatcaStatusTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Lead = Guid.NewGuid();
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Signed = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static FatcaIndicia UsBorn => new(BornInTheUnitedStates: true);

    private static Result<FatcaDeclaration> Record(
        FatcaIndicia? indicia = null,
        FatcaDocumentation documentation = FatcaDocumentation.None,
        string? usTin = null,
        string? evidence = null,
        string? selfCert = "s3://self-cert/2026-10-01.pdf",
        DateTimeOffset? signedAt = null,
        string? refusalReason = null)
        => FatcaDeclaration.Record(
            tenantId: Tenant, leadId: Lead,
            subjectType: QualificationSubjectType.Principal, subjectId: Lead,
            indicia: indicia ?? FatcaIndicia.None,
            documentation: documentation,
            recordedBy: Actor, now: Now,
            usTin: usTin, selfCertificationDocRef: selfCert, evidenceDocRef: evidence,
            signedAt: signedAt ?? (documentation == FatcaDocumentation.Refused ? null : Signed),
            refusalReason: refusalReason);

    // ── The status rule, in its evaluation order ──────────────────────────

    [Fact]
    public void No_index_and_nothing_produced_is_a_non_US_person()
    {
        Record().Value.Status.Should().Be(FatcaStatus.NonUsPerson);
    }

    [Fact]
    public void An_index_with_nothing_produced_is_pending()
    {
        Record(UsBorn).Value.Status.Should().Be(FatcaStatus.IndiciaPending);
    }

    [Fact]
    public void A_W9_is_a_US_person()
    {
        Record(UsBorn, FatcaDocumentation.FormW9, usTin: "123-45-6789")
            .Value.Status.Should().Be(FatcaStatus.UsPerson);
    }

    [Fact]
    public void A_W9_with_NO_index_is_still_a_US_person()
    {
        // An index is an INDICATOR; a W-9 is the person stating the fact. A statement beats the
        // absence of a hint — and someone volunteering a W-9 is reportable whether or not an
        // indicator happened to be ticked.
        Record(FatcaIndicia.None, FatcaDocumentation.FormW9, usTin: "123-45-6789")
            .Value.Status.Should().Be(FatcaStatus.UsPerson);
    }

    [Fact]
    public void A_W8BEN_against_a_declared_index_cures_it()
    {
        Record(UsBorn, FatcaDocumentation.FormW8Ben, evidence: "s3://evidence/passport.pdf")
            .Value.Status.Should().Be(FatcaStatus.IndiciaCured);
    }

    [Fact]
    public void A_W8BEN_with_NO_index_is_a_non_US_person_not_cured()
    {
        // IndiciaCured would assert that indices were lifted which never existed.
        Record(FatcaIndicia.None, FatcaDocumentation.FormW8Ben, evidence: "s3://evidence/passport.pdf")
            .Value.Status.Should().Be(FatcaStatus.NonUsPerson);
    }

    [Fact]
    public void A_refusal_is_recalcitrant_whatever_else_is_true()
    {
        Record(UsBorn, FatcaDocumentation.Refused, refusalReason: "refuse de signer")
            .Value.Status.Should().Be(FatcaStatus.Recalcitrant);

        Record(FatcaIndicia.None, FatcaDocumentation.Refused, refusalReason: "refuse de signer")
            .Value.Status.Should().Be(FatcaStatus.Recalcitrant);
    }

    // ── What the documentation requires ───────────────────────────────────

    [Fact]
    public void A_W9_without_a_TIN_is_refused()
    {
        // The form's whole purpose missing: the person is reportable and there is nothing to
        // report them under.
        Record(UsBorn, FatcaDocumentation.FormW9)
            .Error.Should().Be("FATCA_TIN_REQUIRED");
    }

    [Fact]
    public void A_W8BEN_without_evidence_is_refused()
    {
        // The form alone asserts non-US status; the evidence is what lifts the index that
        // contradicted it (BNF-B-11).
        Record(UsBorn, FatcaDocumentation.FormW8Ben)
            .Error.Should().Be("FATCA_EVIDENCE_REQUIRED");
    }

    [Fact]
    public void A_refusal_without_a_reason_is_refused()
    {
        Record(UsBorn, FatcaDocumentation.Refused)
            .Error.Should().Be("FATCA_REFUSAL_REASON_REQUIRED");
    }

    // ── The self-certification artefact (F02.10) ──────────────────────────

    [Fact]
    public void A_declaration_with_no_self_certification_is_refused()
    {
        // Including a plain "no indices": that negative statement is exactly what an institution
        // has to be able to produce.
        Record(selfCert: null).Error.Should().Be("FATCA_SELF_CERTIFICATION_REQUIRED");
    }

    [Fact]
    public void A_declaration_with_no_signature_date_is_refused()
    {
        // The factory is called directly here: the Record helper above defaults signedAt, so
        // passing null through it would not express "no signature" at all.
        FatcaDeclaration.Record(
                tenantId: Tenant, leadId: Lead,
                subjectType: QualificationSubjectType.Principal, subjectId: Lead,
                indicia: FatcaIndicia.None,
                documentation: FatcaDocumentation.None,
                recordedBy: Actor, now: Now,
                selfCertificationDocRef: "s3://self-cert.pdf",
                signedAt: null)
            .Error.Should().Be("FATCA_SIGNATURE_REQUIRED");
    }

    [Fact]
    public void A_signature_in_the_future_is_refused()
    {
        Record(signedAt: Now.AddDays(1)).Error.Should().Be("FATCA_SIGNATURE_IN_FUTURE");
    }

    [Fact]
    public void A_refusal_needs_neither_a_self_certification_nor_a_signature()
    {
        // There is nothing to certify: the person declined to.
        Record(UsBorn, FatcaDocumentation.Refused,
                selfCert: null, signedAt: null, refusalReason: "refuse de signer")
            .IsSuccess.Should().BeTrue();
    }

    // ── NotApplicable belongs to the tenant, not to the declaration ───────

    [Fact]
    public void The_stored_status_is_never_NotApplicable()
    {
        // It is a property of the TENANT. Storing it would mean rewriting every declaration when
        // the toggle moves, and recomputing them from unchanged data when it moves back.
        foreach (var documentation in Enum.GetValues<FatcaDocumentation>())
        {
            var declaration = Record(
                UsBorn, documentation,
                usTin: "123-45-6789",
                evidence: "s3://evidence.pdf",
                refusalReason: "refuse",
                selfCert: documentation == FatcaDocumentation.Refused ? null : "s3://cert.pdf",
                signedAt: documentation == FatcaDocumentation.Refused ? null : Signed);

            declaration.Value.Status.Should().NotBe(FatcaStatus.NotApplicable);
        }
    }

    [Fact]
    public void The_effective_status_is_NotApplicable_when_the_tenant_is_not_subject_to_FATCA()
    {
        var declaration = Record(UsBorn, FatcaDocumentation.FormW9, usTin: "123-45-6789").Value;

        declaration.EffectiveStatus(fatcaEnabled: true).Should().Be(FatcaStatus.UsPerson);
        declaration.EffectiveStatus(fatcaEnabled: false).Should().Be(FatcaStatus.NotApplicable);
        declaration.Status.Should().Be(FatcaStatus.UsPerson, "the stored value is untouched");
    }

    // ── Who the compliance officer hears about ────────────────────────────

    [Theory]
    [InlineData(FatcaStatus.UsPerson)]
    [InlineData(FatcaStatus.Recalcitrant)]
    public void UsPerson_and_Recalcitrant_require_a_compliance_review(FatcaStatus expected)
    {
        var declaration = expected == FatcaStatus.UsPerson
            ? Record(UsBorn, FatcaDocumentation.FormW9, usTin: "123-45-6789").Value
            : Record(UsBorn, FatcaDocumentation.Refused,
                     selfCert: null, signedAt: null, refusalReason: "refuse").Value;

        declaration.Status.Should().Be(expected);
        declaration.RequiresComplianceReview.Should().BeTrue();
    }

    [Fact]
    public void A_cured_or_clean_declaration_needs_no_review()
    {
        Record().Value.RequiresComplianceReview.Should().BeFalse();
        Record(UsBorn, FatcaDocumentation.FormW8Ben, evidence: "s3://e.pdf")
            .Value.RequiresComplianceReview.Should().BeFalse();
    }

    [Fact]
    public void Only_a_pending_declaration_still_owes_documentation()
    {
        Record(UsBorn).Value.IsDocumentationOutstanding.Should().BeTrue();
        Record().Value.IsDocumentationOutstanding.Should().BeFalse();
        Record(UsBorn, FatcaDocumentation.FormW9, usTin: "123-45-6789")
            .Value.IsDocumentationOutstanding.Should().BeFalse();
    }

    // ── Indicia ───────────────────────────────────────────────────────────

    [Fact]
    public void Each_declared_index_stays_separately_identifiable()
    {
        // The evidence that cures an index depends on which index it is, so a compliance officer
        // reviewing a cure has to know what was being cured.
        var indicia = new FatcaIndicia(
            UsNationalityOrResidence: true,
            UsPhoneNumber: true,
            CareOfOrPosteRestanteAsSoleAddress: true);

        indicia.Any.Should().BeTrue();
        indicia.Declared().Should().BeEquivalentTo(
            ["UsNationalityOrResidence", "UsPhoneNumber", "CareOfOrPosteRestanteAsSoleAddress"]);
    }

    [Fact]
    public void An_empty_indicia_set_declares_nothing()
    {
        FatcaIndicia.None.Any.Should().BeFalse();
        FatcaIndicia.None.Declared().Should().BeEmpty();
    }

    [Fact]
    public void All_seven_indices_of_the_specification_are_modelled()
    {
        // A guard against one being dropped in a refactor: BNF-B-11 enumerates exactly seven.
        typeof(FatcaIndicia).GetProperties()
            .Count(p => p.PropertyType == typeof(bool))
            .Should().Be(8, "seven indices plus the Any summary");
    }

    // ── Superseding (BNF-B-11) ────────────────────────────────────────────

    [Fact]
    public void Superseding_historises_rather_than_erases()
    {
        var first = Record(UsBorn).Value;
        var second = Record(UsBorn, FatcaDocumentation.FormW9, usTin: "123-45-6789").Value;

        first.Supersede(second.Id, Now).IsSuccess.Should().BeTrue();

        first.IsActive.Should().BeFalse();
        first.SupersededAt.Should().Be(Now);
        first.SupersededByDeclarationId.Should().Be(second.Id);
        first.Status.Should().Be(FatcaStatus.IndiciaPending,
            "what the person certified in March stays March's answer");
    }

    [Fact]
    public void Superseding_twice_is_refused()
    {
        var first = Record(UsBorn).Value;
        first.Supersede(Guid.NewGuid(), Now);

        first.Supersede(Guid.NewGuid(), Now)
            .Error.Should().Be("FATCA_DECLARATION_ALREADY_SUPERSEDED");
    }
}
