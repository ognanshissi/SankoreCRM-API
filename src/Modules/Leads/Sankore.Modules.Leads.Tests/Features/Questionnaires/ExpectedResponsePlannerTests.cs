namespace Sankore.Modules.Leads.Tests.Features.Questionnaires;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <see cref="ExpectedResponsePlanner"/> — "ne jamais oublier un assuré" (BNF-B-08), planned
/// from the PROJECT's product.
/// </summary>
public sealed class ExpectedResponsePlannerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _productLine = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public ExpectedResponsePlannerTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        LineIs("SANTE-01", nameof(ProductCategory.HealthInsurance));
        _admin.ResolveProductLineAsync(_tenant, Product, Arg.Any<CancellationToken>())
            .Returns(_productLine);
    }

    private const string Product = "Assurance santé";

    public void Dispose() => _factory.Dispose();

    private void LineIs(string code, string category) =>
        _admin.GetBeneficiaryPolicyAsync(_tenant, _productLine, Arg.Any<CancellationToken>())
            .Returns(new BeneficiaryPolicyDto(
                _productLine, code, category, true, false,
                [], null, null, [], null, null));

    private ExpectedResponsePlanner Planner(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
        => new(db, _admin, TimeProvider.System);

    /// <summary>A null <paramref name="productLineId"/> is a lead whose need names no product yet.</summary>
    private Lead LeadWith(Guid? productLineId, int children = 0, bool spouse = false)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: productLineId is null ? "Unknown" : Product,
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0), preferredAgencyId: null,
            clock: TimeProvider.System,
            firstName: "Aya", lastName: "Kouassi", ownerId: Guid.NewGuid());

        if (spouse)
            lead.AddHouseholdMember(
                HouseholdMemberType.Spouse, SpouseUnionType.Married,
                PersonIdentity.Create("Kouassi", "Awa"), null, true, Now);

        for (var i = 0; i < children; i++)
            lead.AddHouseholdMember(
                HouseholdMemberType.Child, null,
                PersonIdentity.Create("Kouassi", $"Enfant{i}"), null, true, Now);

        return lead;
    }

    private QualificationTemplate PublishedTemplate(
        QualificationSubjectScope scope,
        bool health = true,
        string? productCode = null,
        ProductCategory? category = ProductCategory.HealthInsurance)
    {
        var template = QualificationTemplate.Create(
            _tenant, "Questionnaire santé", Now, productCategory: category, productCode: productCode);

        template.AddQuestion("Fumez-vous ?", QuestionType.YesNo, 10, true);
        template.SetSubjectScope(scope, health, false);
        template.Publish(Now);
        return template;
    }

    /// <summary>The lead, ONE open project on its product (none named when the lead has no line), and the templates.</summary>
    private async Task SeedAsync(Lead lead, params QualificationTemplate[] templates)
    {
        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        db.Opportunities.Add(Opportunity.CreateFromLead(
            _tenant, lead.Id, "Projet santé", lead.InterestedProduct == "Unknown" ? null : Product,
            ownerId: null, TimeProvider.System));
        db.QualificationTemplates.AddRange(templates);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static Task<Opportunity> ProjectAsync(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
        => db.Opportunities.FirstAsync(CancellationToken.None);

    private async Task<List<QualificationResponse>> ResponsesAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.QualificationResponses.ToListAsync(CancellationToken.None);
    }

    // ── The core promise: nobody is forgotten ─────────────────────────────

    [Fact]
    public async Task EachInsuredPerson_plans_the_principal_and_every_active_member()
    {
        var lead = LeadWith(_productLine, children: 2, spouse: true);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

            (await Planner(db).SyncAsync(tracked, CancellationToken.None)).Should().Be(4);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var responses = await ResponsesAsync();
        responses.Should().HaveCount(4, "the principal, the spouse and two children");
        responses.Count(r => r.SubjectType == QualificationSubjectType.Principal).Should().Be(1);
        responses.Count(r => r.SubjectType == QualificationSubjectType.HouseholdMember).Should().Be(3);
        responses.Should().OnlyContain(r => r.Status == QualificationResponseStatus.ToComplete);
        responses.Should().OnlyContain(r => r.IsHealthData);
    }

    [Fact]
    public async Task A_member_added_later_gets_its_own_questionnaire()
    {
        var lead = LeadWith(_productLine);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

            await Planner(db).SyncAsync(tracked, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await ResponsesAsync()).Should().HaveCount(1, "only the principal so far");

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

            tracked.AddHouseholdMember(
                HouseholdMemberType.Child, null,
                PersonIdentity.Create("Kouassi", "Kofi"), null, true, Now);

            (await Planner(db).SyncAsync(tracked, CancellationToken.None)).Should().Be(1);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await ResponsesAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Syncing_twice_adds_nothing()
    {
        // It runs on every household change, so idempotence is not an optimisation.
        var lead = LeadWith(_productLine, children: 1);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        for (var run = 0; run < 3; run++)
        {
            await using var db = _factory.CreateContext();
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

            await Planner(db).SyncAsync(tracked, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await ResponsesAsync()).Should().HaveCount(2);
    }

    // ── Scopes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SpouseOnly_plans_the_spouse_and_nobody_else()
    {
        var lead = LeadWith(_productLine, children: 2, spouse: true);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.SpouseOnly));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        var plan = await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None);

        plan.Should().ContainSingle();
    }

    [Fact]
    public async Task ChildrenOnly_plans_each_child()
    {
        var lead = LeadWith(_productLine, children: 3, spouse: true);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.ChildrenOnly));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None)).Should().HaveCount(3);
    }

    [Fact]
    public async Task A_lead_scoped_template_is_planned_ONCE_for_the_file()
    {
        // The questionnaire the lead's « Qualification » tab used to ask: answered once for the
        // whole file, not expected of each person — whatever the household.
        var lead = LeadWith(_productLine, children: 2);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.Lead, health: false));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        var plan = await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None);

        var dossier = plan.Should().ContainSingle().Subject;
        dossier.IsDossier.Should().BeTrue();
        dossier.SubjectType.Should().Be(QualificationSubjectType.Lead);
        dossier.SubjectId.Should().Be(lead.Id, "an answer given on the lead before projects held it must still match");
    }

    [Fact]
    public async Task Only_the_most_specific_file_questionnaire_is_planned()
    {
        // Product's own, else its family's, else the generic one — never several.
        var lead = LeadWith(_productLine);
        var family = PublishedTemplate(QualificationSubjectScope.Lead, health: false);
        var own = PublishedTemplate(QualificationSubjectScope.Lead, health: false, productCode: "SANTE-01");
        await SeedAsync(lead, family, own);

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        var plan = await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None);

        plan.Should().ContainSingle().Which.TemplateId.Should().Be(own.Id);
    }

    [Fact]
    public async Task A_project_on_a_client_with_no_lead_asks_its_holder_only()
    {
        // No household in this module for a client: the holder is asked, as CustomerPrincipal;
        // the spouse and children scopes have nobody to name.
        var customerId = Guid.NewGuid();
        await using (var seed = _factory.CreateContext())
        {
            seed.Opportunities.Add(Opportunity.CreateForCustomer(
                _tenant, customerId, "Projet santé", Product, ownerId: null, TimeProvider.System));
            seed.QualificationTemplates.AddRange(
                PublishedTemplate(QualificationSubjectScope.EachInsuredPerson),
                PublishedTemplate(QualificationSubjectScope.ChildrenOnly));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var db = _factory.CreateContext();
        var plan = await Planner(db).PlanAsync(await ProjectAsync(db), lead: null, CancellationToken.None);

        var holder = plan.Should().ContainSingle().Subject;
        holder.SubjectType.Should().Be(QualificationSubjectType.CustomerPrincipal);
        holder.SubjectId.Should().Be(customerId);
    }

    [Fact]
    public async Task Each_open_project_of_the_lead_gets_its_own_rows()
    {
        var lead = LeadWith(_productLine);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.PrincipalOnly));
        await using (var second = _factory.CreateContext())
        {
            second.Opportunities.Add(Opportunity.CreateFromLead(
                _tenant, lead.Id, "Second projet", Product, ownerId: null, TimeProvider.System));
            await second.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);
            (await Planner(db).SyncAsync(tracked, CancellationToken.None)).Should().Be(2);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var responses = await ResponsesAsync();
        responses.Select(r => r.OpportunityId).Distinct().Should().HaveCount(2);
        responses.Should().OnlyContain(r => r.LeadId == lead.Id);
    }

    [Fact]
    public async Task A_removed_member_is_no_longer_expected_to_answer()
    {
        // "ses réponses passent hors périmètre de complétude, sans suppression" — the expectation
        // goes, the stored row stays.
        var lead = LeadWith(_productLine, children: 1);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);
            await Planner(db).SyncAsync(tracked, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using var read = _factory.CreateContext();
        var reread = await read.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        reread.RemoveHouseholdMember(
            reread.HouseholdMembers[0].Id, "sorti du foyer", Now);

        var plan = await Planner(read).PlanAsync(await ProjectAsync(read), reread, CancellationToken.None);

        plan.Should().ContainSingle("only the principal remains expected");
        (await ResponsesAsync()).Should().HaveCount(2, "the removed member's row is kept");
    }

    // ── Matching the product line ─────────────────────────────────────────

    [Fact]
    public async Task A_template_pinned_to_the_product_CODE_matches()
    {
        var lead = LeadWith(_productLine);
        await SeedAsync(lead, PublishedTemplate(
            QualificationSubjectScope.PrincipalOnly, productCode: "SANTE-01"));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_template_for_another_category_does_not_match()
    {
        var lead = LeadWith(_productLine);
        await SeedAsync(lead, PublishedTemplate(
            QualificationSubjectScope.PrincipalOnly, health: false, category: ProductCategory.Loan));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_with_no_product_expects_nothing()
    {
        // Not an error: a project is opened on a need « à préciser » before anybody picks a product.
        var lead = LeadWith(productLineId: null, children: 2);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        (await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_M12_outage_plans_nothing_and_does_not_throw()
    {
        // The caller is a household write that must still succeed — a missing ToComplete row is
        // recreated on the next change, whereas failing the write would lose the member.
        _admin.GetBeneficiaryPolicyAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<BeneficiaryPolicyDto?>>(_ => throw new HttpRequestException("M12 down"));

        var lead = LeadWith(_productLine, children: 1);
        await SeedAsync(lead, PublishedTemplate(QualificationSubjectScope.EachInsuredPerson));

        await using var db = _factory.CreateContext();
        var tracked = await db.Leads.AsTracking()
            .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);

        var act = async () => await Planner(db).SyncAsync(tracked, CancellationToken.None);
        await act.Should().NotThrowAsync();

        // …while a GATE learns that the plan is unknown, rather than reading it as empty.
        var plan = async () => await Planner(db).PlanAsync(await ProjectAsync(db), tracked, CancellationToken.None);
        await plan.Should().ThrowAsync<ProductLineUnavailableException>();
    }

    // ── The frozen version (BNF-B-07) ─────────────────────────────────────

    [Fact]
    public async Task The_template_version_is_frozen_on_the_response()
    {
        var template = PublishedTemplate(QualificationSubjectScope.PrincipalOnly);
        template.RevertToDraft();
        template.Publish(Now);          // version 2

        var lead = LeadWith(_productLine);
        await SeedAsync(lead, template);

        await using (var db = _factory.CreateContext())
        {
            var tracked = await db.Leads.AsTracking()
                .Include(l => l.HouseholdMembers).SingleAsync(CancellationToken.None);
            await Planner(db).SyncAsync(tracked, CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var response = (await ResponsesAsync()).Single();
        response.QuestionnaireVersion.Should().Be(2,
            "a response re-read through the live template would be re-interpreted against "
            + "questions the person was never asked");
    }
}
