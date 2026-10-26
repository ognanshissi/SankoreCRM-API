namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities;
using Sankore.Modules.Leads.Features.Opportunities.OpenHolding;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// « Ouvrir le contrat » sur un projet gagné : la reprise de ce que la clôture a tenté une fois et
/// tu en cas d'échec — un projet d'assurance gagné sans contrat n'avait plus aucun chemin vers un.
/// </summary>
public sealed class OpenProjectHoldingTests : IDisposable
{
    private const string Product = "OBS_SERENITE";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly Guid _client = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public OpenProjectHoldingTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _user.Id.Returns(Guid.NewGuid());
        _user.TenantId.Returns(_tenant);
        _admin.ResolveProductLineAsync(_tenant, Product, Arg.Any<CancellationToken>()).Returns(_line);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_won_project_opens_its_contract_on_the_client_and_line()
    {
        var holdingId = Guid.NewGuid();
        _customers.CreateHoldingFromOpportunityAsync(Arg.Any<CreateHoldingFromOpportunityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(holdingId));
        var projectId = await SeedAsync(won: true);

        var result = await OpenAsync(projectId);

        result.Value.Should().Be(holdingId);
        await _customers.Received(1).CreateHoldingFromOpportunityAsync(
            Arg.Is<CreateHoldingFromOpportunityRequest>(r =>
                r.ClientId == _client && r.ProductLineId == _line && r.SourceOpportunityId == projectId
                && r.ClosedByUserId == _user.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_open_project_owes_no_contract_yet()
    {
        var projectId = await SeedAsync(won: false);

        (await OpenAsync(projectId)).Error.Should().Be(OpportunityErrors.NotWon);
        await _customers.DidNotReceiveWithAnyArgs().CreateHoldingFromOpportunityAsync(default!, default);
    }

    [Fact]
    public async Task M01s_refusal_is_reported_rather_than_swallowed()
    {
        _customers.CreateHoldingFromOpportunityAsync(Arg.Any<CreateHoldingFromOpportunityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("HOLDING_ALREADY_HELD"));
        var projectId = await SeedAsync(won: true);

        (await OpenAsync(projectId)).Error.Should().Be("HOLDING_ALREADY_HELD",
            "the agent asked again, so they are told why — unlike the closing, which must not fail over it");
    }

    [Fact]
    public async Task A_product_outside_the_catalogue_is_named()
    {
        _admin.ResolveProductLineAsync(_tenant, Product, Arg.Any<CancellationToken>()).Returns((Guid?)null);
        var projectId = await SeedAsync(won: true);

        (await OpenAsync(projectId)).Error.Should().Be(OpportunityErrors.ProductNotInCatalogue);
    }

    private async Task<Guid> SeedAsync(bool won)
    {
        await using var db = _factory.CreateContext();
        var project = Opportunity.CreateForCustomer(
            _tenant, _client, "Projet — Awa Koné", Product, ownerId: null, TimeProvider.System);
        if (won)
            project.Close(OpportunityStage.ClosedWon, DateTimeOffset.UtcNow).IsSuccess.Should().BeTrue();
        db.Opportunities.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    private async Task<Result<Guid>> OpenAsync(Guid projectId)
    {
        await using var db = _factory.CreateContext();
        return await new OpenProjectHoldingHandler(db, _customers, _admin, _user)
            .Handle(new OpenProjectHoldingCommand(projectId), CancellationToken.None);
    }
}
