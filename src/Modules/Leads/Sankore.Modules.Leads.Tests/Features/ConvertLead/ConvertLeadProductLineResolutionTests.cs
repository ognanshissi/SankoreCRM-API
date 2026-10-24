namespace Sankore.Modules.Leads.Tests.Features.ConvertLead;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.Enrichment.GetCompleteness;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Le dossier client s'ouvre pour un PROJET, et c'est le produit de ce projet qui décide du KYC
/// (propriétaire, 2026-10-24 : le lead ne stocke plus de ligne produit). Avant, la ligne du lead
/// décidait : un lead capté pour un crédit qui ouvrait un projet sur un produit sans KYC créait un
/// client « en attente de KYC » et demandait un KYC dont la vente n'avait pas besoin.
/// </summary>
public sealed class ConvertLeadProductLineResolutionTests : IDisposable
{
    private const string Credit = "Crédit commerçant";
    private const string Card = "Carte de fidélité";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _creditLine = Guid.NewGuid();
    private readonly Guid _cardLine = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ConvertLeadProductLineResolutionTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _administration.GetComplianceSettingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(TestCompleteness.FatcaOff);
        _administration.ResolveProductLineAsync(_tenantId, Credit, Arg.Any<CancellationToken>()).Returns(_creditLine);
        _administration.ResolveProductLineAsync(_tenantId, Card, Arg.Any<CancellationToken>()).Returns(_cardLine);
        _administration.GetBeneficiaryPolicyAsync(_tenantId, _creditLine, Arg.Any<CancellationToken>())
            .Returns(Policy(_creditLine, "CREDCOM", "Loan", "Full"));
        _administration.GetBeneficiaryPolicyAsync(_tenantId, _cardLine, Arg.Any<CancellationToken>())
            .Returns(Policy(_cardLine, "CARTE", "Standard", KycRequirement.None));
        _customers.CreateFromLeadAsync(Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new CreateFromLeadResult(Guid.NewGuid(), "N-1", AlreadyExisted: false)));
    }

    public void Dispose() => _factory.Dispose();

    private static BeneficiaryPolicyDto Policy(Guid id, string code, string category, string? tier, params string[] required)
        => new(id, code, category, true, true, [], null, null, required, tier, null);

    private async Task<Lead> SeedAsync(string asked, params string[] projects)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenantId).WithAgency(Guid.NewGuid()).WithProduct(asked).Build();
        db.Leads.Add(lead);
        foreach (var product in projects)
            db.Opportunities.Add(Opportunity.CreateFromLead(_tenantId, lead.Id, product, product, null, TimeProvider.System));
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<Guid> ProjectAsync(string product)
    {
        await using var db = _factory.CreateContext();
        return db.Opportunities.Single(o => o.Product == product).Id;
    }

    private async Task ConvertAsync(Guid leadId, Guid? projectId = null)
    {
        await using var db = _factory.CreateContext();
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.TenantId.Returns(_tenantId);

        var handler = new ConvertLeadHandler(
            db, user, _customers, _administration, TestLeadEmailIndexer.Instance,
            TestCompleteness.Calculator(db, _administration, _customers), _publisher);

        var result = await handler.Handle(
            new ConvertLeadCommand(leadId, Force: true, ProjectId: projectId), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    private List<KycRequestedIntegrationEvent> KycRequests()
        => _publisher.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<KycRequestedIntegrationEvent>()
            .ToList();

    private CreateFromLeadRequest ClientRequest()
        => _customers.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<CreateFromLeadRequest>()
            .Single();

    [Fact]
    public async Task With_no_project_the_asked_product_reaches_the_kyc_request()
    {
        var lead = await SeedAsync(Credit);

        await ConvertAsync(lead.Id);

        KycRequests().Should().ContainSingle().Which.ProductLineId.Should().Be(_creditLine);
    }

    [Fact]
    public async Task The_single_open_project_decides_not_what_the_lead_asked_for()
    {
        // The defect this pins: asked for a credit, sold a no-KYC card.
        var lead = await SeedAsync(Credit, Card);

        await ConvertAsync(lead.Id);

        KycRequests().Should().BeEmpty("the project's product requires no KYC");
        ClientRequest().KycRequired.Should().BeFalse("the client is created active");
    }

    [Fact]
    public async Task A_named_project_wins_over_the_lead_s_other_projects()
    {
        var lead = await SeedAsync(Card, Card, Credit);

        await ConvertAsync(lead.Id, await ProjectAsync(Credit));

        KycRequests().Should().ContainSingle().Which.ProductLineId.Should().Be(_creditLine);
    }

    [Fact]
    public async Task A_line_recorded_on_the_project_is_used_without_looking_the_label_up()
    {
        var lead = await SeedAsync(Card, "Libellé libre");
        await using (var db = _factory.CreateContext())
        {
            var project = db.Opportunities.Single();
            db.Attach(project);
            project.SetProductLine(_creditLine);
            await db.SaveChangesAsync();
        }

        await ConvertAsync(lead.Id);

        KycRequests().Should().ContainSingle().Which.ProductLineId.Should().Be(_creditLine);
    }

    [Fact]
    public async Task The_completeness_report_applies_the_same_project_as_the_opening()
    {
        // The wizard's gauge used to read the ASKED product's policy while the opening checked the
        // project's: « dossier complet » on screen, then a refusal — or the reverse.
        _administration.GetBeneficiaryPolicyAsync(_tenantId, _cardLine, Arg.Any<CancellationToken>())
            .Returns(Policy(_cardLine, "CARTE", "Standard", KycRequirement.None, PersonFieldCode.BirthDate));
        var lead = await SeedAsync(Credit, Card, Credit);

        await using var db = _factory.CreateContext();
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(Guid.NewGuid());
        user.Roles.Returns([Sankore.Shared.Kernel.Roles.Administrator.Code]);
        var handler = new GetLeadCompletenessHandler(
            db, new LeadAccessGuard(user, _administration),
            TestCompleteness.Calculator(db, _administration, _customers), _administration);

        var forCard = await handler.Handle(new GetLeadCompletenessQuery(lead.Id, await ProjectAsync(Card)), CancellationToken.None);
        var forCredit = await handler.Handle(new GetLeadCompletenessQuery(lead.Id, await ProjectAsync(Credit)), CancellationToken.None);

        forCard.Value.CanConvert.Should().BeFalse("the card's policy requires a birth date");
        forCredit.Value.CanConvert.Should().BeTrue("the credit's policy requires nothing more");
    }
}
