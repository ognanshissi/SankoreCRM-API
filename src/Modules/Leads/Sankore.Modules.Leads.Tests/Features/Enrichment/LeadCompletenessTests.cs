namespace Sankore.Modules.Leads.Tests.Features.Enrichment;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Enrichment;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The single completeness calculator (BNF-B-04): a percentage, what is missing PER PERSON with
/// stable codes, and whether the conversion may proceed.
/// </summary>
public sealed class LeadCompletenessTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public LeadCompletenessTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.FatcaOff);
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private void PolicyRequires(params string[] fields)
        => _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
                 .Returns(new BeneficiaryPolicyDto(
                     ProductLineId: _line,
                     ProductCode: "SANTE-01",
                     Category: nameof(ProductCategory.HealthInsurance),
                     IsActive: true,
                     AllowMultipleHoldings: false,
                     AllowedRoles: [],
                     MaxChildren: null,
                     ChildMaxAge: null,
                     RequiredFieldsBeforeConversion: fields,
                     RequiredKycTier: null,
                     IntegrationSystem: null));

    /// <summary>
    /// The lead stores no product line: <paramref name="lineId"/> is what its asked product
    /// resolves to in the catalogue.
    /// </summary>
    private Lead Capture(Guid? lineId = null, bool withNames = true)
    {
        _admin.ResolveProductLineAsync(_tenant, "Assurance santé", Arg.Any<CancellationToken>())
              .Returns(lineId);

        return Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: Guid.NewGuid(), clock: TimeProvider.System,
            firstName: withNames ? "Aya" : null,
            lastName: withNames ? "Kouassi" : null,
            ownerId: Guid.NewGuid());
    }

    private async Task<LeadCompletenessDto> ComputeAsync(Lead lead, Guid? projectLine = null, bool forProject = false)
    {
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);

        var calculator = TestCompleteness.Calculator(db, _admin);
        return forProject
            ? await calculator.ComputeAsync(tracked, projectLine, CancellationToken.None)
            : await calculator.ComputeAsync(tracked, CancellationToken.None);
    }

    // ── the product line: the project's, else what the lead asked for ───────

    [Fact]
    public async Task A_lead_whose_asked_product_names_no_line_owes_only_what_every_lead_owes()
    {
        // The lead stores no product line (product owner, 2026-10-24); an unknown product is a
        // need « à préciser », which the project's step gate — not this report — refuses to sell.
        var report = await ComputeAsync(Capture(lineId: null));

        report.CanConvert.Should().BeTrue();
        report.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task The_project_line_decides_the_required_fields_whatever_the_lead_asked_for()
    {
        PolicyRequires(PersonFieldCode.BirthDate);

        var report = await ComputeAsync(Capture(lineId: null), projectLine: _line, forProject: true);

        report.CanConvert.Should().BeFalse("the project's product requires a birth date");
        report.Gaps.Should().Contain(g => g.Code == CompletenessCodes.MissingRequiredField);
    }

    // ── required fields, per person ─────────────────────────────────────────

    [Fact]
    public async Task A_missing_required_field_is_reported_against_the_person_who_lacks_it()
    {
        PolicyRequires(PersonFieldCode.BirthDate);

        var lead = Capture(_line);
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi", birthDate: new DateOnly(2015, 4, 2)),
            null, true, Now);

        var report = await ComputeAsync(lead);

        // The principal has no birth date; the child does. BNF-B-04 wants the list PER PERSON.
        report.Gaps.Should().ContainSingle(g => g.Code == CompletenessCodes.MissingRequiredField)
              .Which.Should().Match<CompletenessGap>(g =>
                  g.SubjectLabel == "Le titulaire" && g.Detail == PersonFieldCode.BirthDate);
    }

    [Fact]
    public async Task A_household_member_is_never_asked_for_a_field_only_a_principal_has()
    {
        PolicyRequires(PersonFieldCode.Phone, PersonFieldCode.NationalId);

        var lead = Capture(_line);
        lead.AddHouseholdMember(
            HouseholdMemberType.Spouse, SpouseUnionType.Married,
            PersonIdentity.Create("Kouassi", "Awa"), null, true, Now);

        var report = await ComputeAsync(lead);

        // The phone is satisfied by the lead; the national id is missing, and ONLY for the
        // principal — there is no column for a spouse's, so asking would be an unclosable gap.
        report.Gaps.Should().OnlyContain(g => g.SubjectLabel != "Le conjoint (Awa)");
        report.Gaps.Should().ContainSingle(g => g.Detail == PersonFieldCode.NationalId);
    }

    [Fact]
    public async Task An_inherited_address_gap_points_at_the_principal()
    {
        // A member inheriting the address has no postal fields of its own, and its own update
        // command refuses them (HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS). Telling the agent to fill
        // them on the member would be an instruction the API rejects.
        PolicyRequires(PersonFieldCode.Address);

        var lead = Capture(_line);
        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null, usesPrincipalAddress: true, now: Now);

        var report = await ComputeAsync(lead);

        report.Gaps.Should().Contain(g =>
            g.SubjectLabel == "L'enfant Kofi" && g.ActionHint == "FIX_ON_PRINCIPAL");
    }

    [Fact]
    public async Task A_members_inherited_address_is_satisfied_by_the_principals()
    {
        PolicyRequires(PersonFieldCode.Address);

        var lead = Capture(_line);
        lead.EnrichPrincipalIdentity(
            null, null, null, null, null,
            addressLine: "12 rue des Jardins", postalCode: null, city: null, countryCode: null,
            now: Now);

        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null, usesPrincipalAddress: true, now: Now);

        var report = await ComputeAsync(lead);

        report.Gaps.Should().BeEmpty(
            "the child's address is the principal's, resolved at read time — not missing data");
        report.Percentage.Should().Be(100);
    }

    [Fact]
    public async Task A_required_field_the_platform_cannot_evaluate_blocks_rather_than_passing()
    {
        // BeneficiaryPolicy.Create refuses an unknown code, so this can only come from a policy
        // stored before that validation existed. It must NOT silently pass.
        PolicyRequires("LEGACY_FREE_TEXT_CODE");

        var report = await ComputeAsync(Capture(_line));

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().Contain(g => g.Detail == "LEGACY_FREE_TEXT_CODE");
    }

    // ── PEP: declared, not merely negative ──────────────────────────────────

    [Fact]
    public async Task A_PEP_status_nobody_declared_is_missing_even_though_it_reads_not_exposed()
    {
        PolicyRequires(PersonFieldCode.PepStatus);

        var report = await ComputeAsync(Capture(_line));

        report.Gaps.Should().ContainSingle()
              .Which.Code.Should().Be(CompletenessCodes.PepNotDeclared,
                  "PepStatus.NotExposed is the owned type's default, not an answer anybody gave");
    }

    [Fact]
    public async Task An_explicit_NOT_exposed_satisfies_it()
    {
        PolicyRequires(PersonFieldCode.PepStatus);

        var lead = Capture(_line);
        lead.SetPrincipalPepStatus(PepStatus.NotExposed, Now, postMandateMonths: 12);

        var report = await ComputeAsync(lead);

        report.Gaps.Should().BeEmpty("asking and being told no IS an answer");
    }

    [Fact]
    public async Task A_tenant_with_PEP_vigilance_OFF_is_not_asked_for_a_declaration()
    {
        // The case the switch exists for, and the one that would otherwise contradict the screen:
        // the front offers no PEP block, so being refused the conversion for not declaring one
        // would leave the agent with a gap they cannot close.
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.PepOff);

        PolicyRequires(PersonFieldCode.PepStatus);

        var report = await ComputeAsync(Capture(_line));

        report.Gaps.Should().BeEmpty();
        report.CanConvert.Should().BeTrue();

        // Not counted either, not merely unblocked: a lead whose tenant collects no PEP reads
        // 100%, because a percentage must not depend on a question nobody is asked.
        report.Percentage.Should().Be(100);
    }

    [Fact]
    public async Task An_outage_reading_the_PEP_switch_keeps_vigilance_ON()
    {
        // The opposite direction from the product-line switch, deliberately: there, failing closed
        // means not blocking on a setting nobody could read; here it means keeping an obligation
        // we could not confirm had been waived. Both are "an outage never relaxes a rule".
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns<ComplianceSettingsDto>(_ => throw new InvalidOperationException("M12 down"));

        PolicyRequires(PersonFieldCode.PepStatus);

        var report = await ComputeAsync(Capture(_line));

        report.Gaps.Should().Contain(g => g.Code == CompletenessCodes.PepNotDeclared);
    }

    // ── arithmetic ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lead_of_which_nothing_is_required_is_complete_not_empty()
    {
        PolicyRequires();

        var report = await ComputeAsync(Capture(_line));

        report.Applicable.Should().Be(0, "nothing applies — the product line is not a check of the lead any more");
        report.Percentage.Should().Be(100);
        report.CanConvert.Should().BeTrue();
    }

    [Fact]
    public async Task The_percentage_counts_satisfied_over_applicable()
    {
        PolicyRequires(PersonFieldCode.FirstName, PersonFieldCode.BirthDate, PersonFieldCode.City);

        var report = await ComputeAsync(Capture(_line));

        // Three checks apply: the first name (present), the birth date and the city (both missing).
        report.Applicable.Should().Be(3);
        report.Satisfied.Should().Be(1);
        report.Percentage.Should().Be(33);
    }

    // ── FATCA (BNF-B-11) ────────────────────────────────────────────────────

    [Fact]
    public async Task FATCA_off_is_not_counted_at_all()
    {
        PolicyRequires();

        var report = await ComputeAsync(Capture(_line));

        report.Gaps.Should().NotContain(g =>
            g.Code == CompletenessCodes.FatcaDeclarationMissing);
        report.Applicable.Should().Be(0,
            "BNF-B-11: with FATCA off nothing is required, exposed or COUNTED");
    }

    [Fact]
    public async Task FATCA_on_asks_the_principal_for_a_declaration()
    {
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(new ComplianceSettingsDto(
                  true, "IgaModel1", "ABCDEF.12345.LE.225", "Conversion", false, true, 12));
        PolicyRequires();

        var report = await ComputeAsync(Capture(_line));

        report.Gaps.Should().ContainSingle()
              .Which.Should().Match<CompletenessGap>(g =>
                  g.Code == CompletenessCodes.FatcaDeclarationMissing
                  && g.SubjectLabel == "Le titulaire"
                  && g.Blocking);
        report.CanConvert.Should().BeFalse();
    }

    [Fact]
    public async Task Outstanding_FATCA_documentation_is_reported_but_does_not_block()
    {
        // The indices are declared and the institution is waiting on the client's W-8BEN. That is
        // a compliance queue item, not a reason to stop a subscription.
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(new ComplianceSettingsDto(
                  true, "IgaModel1", "ABCDEF.12345.LE.225", "Conversion", false, true, 12));
        PolicyRequires();

        var lead = Capture(_line);

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);

        var declaration = FatcaDeclaration.Record(
            tenantId: _tenant, leadId: lead.Id,
            subjectType: QualificationSubjectType.Principal, subjectId: lead.Id,
            indicia: new FatcaIndicia(UsNationalityOrResidence: true),
            documentation: FatcaDocumentation.None,
            usTin: null, selfCertificationDocRef: "doc://self-cert/1", evidenceDocRef: null,
            signedAt: Now, refusalReason: null, recordedBy: Guid.NewGuid(), now: Now);

        declaration.IsSuccess.Should().BeTrue(declaration.Error);
        db.FatcaDeclarations.Add(declaration.Value);
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers)
            .FirstAsync(l => l.Id == lead.Id, CancellationToken.None);

        var report = await TestCompleteness.Calculator(db, _admin)
            .ComputeAsync(tracked, CancellationToken.None);

        report.Gaps.Should().ContainSingle()
              .Which.Should().Match<CompletenessGap>(g =>
                  g.Code == CompletenessCodes.FatcaDocumentationOutstanding && !g.Blocking);
        report.CanConvert.Should().BeTrue();
        report.Percentage.Should().BeLessThan(100, "the file genuinely is incomplete");
    }

    // ── an outage never reads as complete ───────────────────────────────────

    [Fact]
    public async Task An_unreachable_product_catalogue_blocks_instead_of_passing()
    {
        _admin.GetBeneficiaryPolicyAsync(_tenant, _line, Arg.Any<CancellationToken>())
              .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new InvalidOperationException("M12 down"));

        var report = await ComputeAsync(Capture(_line));

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().Contain(g => g.Code == CompletenessCodes.CheckUnavailable);
    }

    [Fact]
    public async Task Unreachable_compliance_settings_block_too()
    {
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns<Task<ComplianceSettingsDto>>(_ => throw new InvalidOperationException("M12 down"));
        PolicyRequires();

        var report = await ComputeAsync(Capture(_line));

        report.CanConvert.Should().BeFalse();
        report.Gaps.Should().Contain(g =>
            g.Code == CompletenessCodes.CheckUnavailable && g.Detail == "FATCA_SETTINGS");
    }
}
