namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers the completeness check and the conversion it refuses (BNF-B-08:
/// <c>MISSING_QUESTIONNAIRE_RESPONSES</c>).
/// </summary>
public sealed class CompletenessGateTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _productLine = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public CompletenessGateTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetBeneficiaryPolicyAsync(_tenant, _productLine, Arg.Any<CancellationToken>())
            .Returns(new BeneficiaryPolicyDto(
                _productLine, "SANTE-01", nameof(ProductCategory.HealthInsurance),
                true, false, [], null, null, [], null, null));

        // The completeness calculator asks for these on every lead, and FatcaSubjectPlanner FAILS
        // on an outage — an unstubbed substitute returns a null Task and the whole report becomes
        // COMPLETENESS_CHECK_UNAVAILABLE. Any test supplying its own IAdministrationModule has to
        // stub this; see TestCompleteness.FatcaOff.
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
            .Returns(TestCompleteness.FatcaOff);
    }

    public void Dispose() => _factory.Dispose();

    private Lead LeadWithHousehold()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: Guid.NewGuid(),
            clock: TimeProvider.System, firstName: "Aya", lastName: "Kouassi",
            ownerId: Guid.NewGuid(), productLineId: _productLine);

        lead.AddHouseholdMember(
            HouseholdMemberType.Child, null,
            PersonIdentity.Create("Kouassi", "Kofi"), null, true, Now);

        return lead;
    }

    private QualificationTemplate HealthTemplate()
    {
        var template = QualificationTemplate.Create(
            _tenant, "Questionnaire santé", Now,
            productCategory: ProductCategory.HealthInsurance);

        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);
        template.SetSubjectScope(QualificationSubjectScope.EachInsuredPerson, true, false);
        template.Publish(Now);
        return template;
    }

    private QuestionnaireCompletenessChecker Checker(
        Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
        => new(db, new ExpectedResponsePlanner(db, _admin, TimeProvider.System));

    private ConvertLeadHandler Converter(
        Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.TenantId.Returns(_tenant);

        return new ConvertLeadHandler(
            db, user, _customers, Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>(), TestLeadEmailIndexer.Instance,
            TestCompleteness.Calculator(db, _admin, _customers), Substitute.For<IEventPublisher>());
    }

    // ── The check ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_person_who_has_not_answered_is_reported_with_a_label()
    {
        // BNF-B-04 wants the missing items listed PER PERSON. A bare id is not that.
        var lead = LeadWithHousehold();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        var missing = await Checker(db).MissingAsync(tracked, CancellationToken.None);

        missing.Should().HaveCount(2, "the principal and the child");
        missing.Should().Contain(m => m.SubjectLabel == "Le titulaire");
        missing.Should().Contain(m => m.SubjectLabel == "L'enfant Kofi");
        missing.Should().OnlyContain(m => m.IsHealthQuestionnaire);
        missing.Should().OnlyContain(m => m.TemplateName == "Questionnaire santé");
    }

    [Fact]
    public async Task A_removed_member_owes_nothing()
    {
        var lead = LeadWithHousehold();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        tracked.RemoveHouseholdMember(
            tracked.HouseholdMembers[0].Id, "sorti du foyer", Now);

        var missing = await Checker(db).MissingAsync(tracked, CancellationToken.None);

        missing.Should().ContainSingle("only the principal remains expected");
    }

    [Fact]
    public async Task A_lead_with_no_product_line_owes_nothing()
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System, ownerId: Guid.NewGuid());

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Checker(db).MissingAsync(tracked, CancellationToken.None)).Should().BeEmpty();
    }

    // ── The conversion gate ───────────────────────────────────────────────

    [Fact]
    public async Task Conversion_is_refused_while_a_questionnaire_is_unanswered()
    {
        var lead = LeadWithHousehold();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await Converter(db).Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("the refusal is a result, not an error");
        result.Value.BlockingCode.Should().Be(
            QuestionnaireCompletenessChecker.MissingResponses);
        result.Value.MissingQuestionnaires.Should().HaveCount(2);
        result.Value.CustomerId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task Force_does_NOT_bypass_the_questionnaire_gate()
    {
        // Force exists to override the DUPLICATE gate, which is a judgement about whether two
        // records are the same person. A missing health declaration is not a judgement call — an
        // insurer that receives a subscription without one has been sent an incomplete file.
        var lead = LeadWithHousehold();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        var result = await Converter(db).Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.Value.BlockingCode.Should().Be(QuestionnaireCompletenessChecker.MissingResponses);

        await _customers.DidNotReceive().CreateFromLeadAsync(
            Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_refusal_happens_before_M01_is_asked_to_create_anything()
    {
        // A conversion that created a client and then refused would leave it behind — the
        // SourceLeadId idempotency hands the same one back on retry, which is correct but means
        // the refusal already had a side effect.
        var lead = LeadWithHousehold();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(HealthTemplate());
        await db.SaveChangesAsync(CancellationToken.None);

        await Converter(db).Handle(new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        await _customers.DidNotReceiveWithAnyArgs().CreateFromLeadAsync(default!, default);

        var reread = await db.Leads.AsNoTracking()
            .SingleAsync(l => l.Id == lead.Id, CancellationToken.None);
        reread.Status.Should().NotBe(LeadStatus.Converted);
    }

    [Fact]
    public async Task A_lead_whose_people_have_all_answered_is_not_blocked_by_this_gate()
    {
        var lead = LeadWithHousehold();
        var template = HealthTemplate();

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.QualificationTemplates.Add(template);
        await db.SaveChangesAsync(CancellationToken.None);

        var childId = lead.ActiveHouseholdMembers.Single().Id;

        foreach (var (type, subject) in new[]
                 {
                     (QualificationSubjectType.Principal, lead.Id),
                     (QualificationSubjectType.HouseholdMember, childId),
                 })
        {
            db.QualificationResponses.Add(QualificationResponse.Create(
                tenantId: _tenant, leadId: lead.Id, templateId: template.Id,
                answeredBy: Guid.NewGuid(), clock: TimeProvider.System,
                answers: [(Guid.NewGuid(), "Non")], computedScore: 0,
                questionnaireVersion: template.Version,
                subjectType: type, subjectId: subject, isHealthData: true));
        }

        await db.SaveChangesAsync(CancellationToken.None);

        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Checker(db).MissingAsync(tracked, CancellationToken.None)).Should().BeEmpty();
    }
}
