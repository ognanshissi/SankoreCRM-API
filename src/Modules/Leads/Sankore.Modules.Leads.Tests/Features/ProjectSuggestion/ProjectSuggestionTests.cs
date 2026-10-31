namespace Sankore.Modules.Leads.Tests.Features.ProjectSuggestion;

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectSuggestion;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A lead whose « produit souhaité » looks like a catalogue product gets a proposal to open a
/// project for it (product owner, 2026-10-31) — by code, by name however typed, or by description;
/// never for a product an open project already carries.
/// </summary>
public sealed class ProjectSuggestionTests : IDisposable
{
    private static readonly ProductSummary Commercant =
        new("CRED-COM", "Crédit commerçant", "Loan", true, Guid.NewGuid(), Description: "Financement du stock et du fonds de roulement des commerçants");
    private static readonly ProductSummary Scolaire =
        new("CRED-SCO", "Crédit scolaire", "Loan", true, Guid.NewGuid(), Description: "Frais de rentrée, fournitures et scolarité des enfants");
    private static readonly ProductSummary Epargne =
        new("EPG-LIB", "Épargne libre", "Savings", true, Guid.NewGuid());
    private static readonly ProductSummary Retired =
        new("OLD-01", "Crédit commerçant ancien", "Loan", false, Guid.NewGuid());
    private static readonly ProductSummary[] Catalogue = [Commercant, Scolaire, Epargne, Retired];

    private static ProductMatch? Best(string interest) => ProductMatcher.Rank(interest, Catalogue).FirstOrDefault();

    [Theory]
    [InlineData("CRED-COM", ProductMatchBasis.Code)]
    [InlineData("cred com", ProductMatchBasis.Code)]
    [InlineData("credit commercant", ProductMatchBasis.Name)]
    [InlineData("Commerçant crédit", ProductMatchBasis.Name)]
    [InlineData("crédit commerçant pour ma boutique", ProductMatchBasis.SimilarName)]
    [InlineData("credi comercant", ProductMatchBasis.SimilarName)]
    [InlineData("financement de mon stock", ProductMatchBasis.Description)]
    public void The_product_is_recognised_however_the_prospect_named_it(string interest, ProductMatchBasis basis)
    {
        var best = Best(interest);

        best.Should().NotBeNull();
        best!.Product.Code.Should().Be("CRED-COM");
        best.Basis.Should().Be(basis);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("assurance moto")]
    public void Nothing_is_proposed_when_nothing_looks_like_it(string interest)
        => Best(interest).Should().BeNull();

    [Fact]
    public void A_retired_product_is_never_proposed()
        => ProductMatcher.Rank("Crédit commerçant ancien", Catalogue).Should().NotContain(m => m.Product.Code == "OLD-01");

    [Fact]
    public void An_exact_name_ranks_before_a_partial_one()
        => ProductMatcher.Rank("crédit scolaire", Catalogue).First().Product.Code.Should().Be("CRED-SCO");

    // ── The query ─────────────────────────────────────────────────────────

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public ProjectSuggestionTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.ListProductsAsync(_tenant, null, Arg.Any<CancellationToken>()).Returns(Catalogue);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedAsync(string product, string? openProjectProduct = null)
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create().WithTenant(_tenant).WithProduct(product).Build();
        db.Leads.Add(lead);
        if (openProjectProduct is not null)
            db.Opportunities.Add(Opportunity.CreateFromLead(_tenant, lead.Id, "Projet", openProjectProduct, null, TimeProvider.System));
        await db.SaveChangesAsync();
        return lead;
    }

    private async Task<ProjectSuggestionDto?> SuggestAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        var result = await new GetProjectSuggestionHandler(db, _admin).Handle(new GetProjectSuggestionQuery(leadId), CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    [Fact]
    public async Task A_lead_without_a_project_gets_the_proposal()
    {
        var lead = await SeedAsync("credit commercant");

        var suggestion = await SuggestAsync(lead.Id);

        suggestion!.InterestedProduct.Should().Be("credit commercant");
        suggestion.Products.First().Name.Should().Be("Crédit commerçant");
    }

    [Fact]
    public async Task No_proposal_for_a_product_an_open_project_already_carries()
    {
        var lead = await SeedAsync("credit commercant", openProjectProduct: "Crédit commerçant");

        (await SuggestAsync(lead.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_project_for_another_product_does_not_hide_the_proposal()
    {
        var lead = await SeedAsync("credit commercant", openProjectProduct: "Épargne libre");

        (await SuggestAsync(lead.Id))!.Products.Should().Contain(p => p.Code == "CRED-COM");
    }

    [Fact]
    public async Task An_agent_asking_about_a_lead_they_do_not_hold_gets_not_found()
    {
        // A lead read like any other: an agent sees only the leads they hold (lead:read:all aside).
        var lead = await SeedAsync("credit commercant");
        var agent = Substitute.For<ICurrentUser>();
        agent.Id.Returns(Guid.NewGuid());
        agent.TenantId.Returns(_tenant);
        agent.IsAuthenticated.Returns(true);
        agent.Roles.Returns([Roles.Agent.Code]);
        agent.Permissions.Returns([]);
        await using var db = _factory.CreateContext();
        var services = new ServiceCollection()
            .AddSingleton(agent).AddSingleton(db).AddSingleton(new LeadVisibility(agent))
            .BuildServiceProvider();

        var refused = await new LeadVisibilityBehavior<GetProjectSuggestionQuery, Result<ProjectSuggestionDto?>>(services)
            .Handle(new GetProjectSuggestionQuery(lead.Id), () => throw new InvalidOperationException("must not run"), default);

        refused.Error.Should().Be("LEAD_NOT_FOUND");
    }
}
