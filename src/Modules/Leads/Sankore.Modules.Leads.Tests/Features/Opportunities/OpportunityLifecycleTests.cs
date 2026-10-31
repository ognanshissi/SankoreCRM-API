namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.AdvanceStage;
using Sankore.Modules.Leads.Features.Opportunities.CloseOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.GetOpportunity;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Le cycle de vie d'une demande : les étapes, la clôture, et le contrat que « gagnée » ouvre.
///
/// <para>
/// Ce qui est épinglé ici en priorité, ce sont les cas où le serveur doit <b>quand même</b>
/// clore : une demande gagnée dont le produit ne se résout pas, ou dont M01 refuse la
/// souscription, reste gagnée. L'inverse — perdre une décision commerciale parce qu'un autre
/// module n'a pas répondu — est la seule défaillance que ce chemin ne doit pas avoir.
/// </para>
/// </summary>
public sealed class OpportunityLifecycleTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-14T09:00:00Z"));

    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IAdministrationModule _administration = Substitute.For<IAdministrationModule>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public OpportunityLifecycleTests()
    {
        _factory = new TestDbContextFactory(_tenantId);
        _currentUser.Id.Returns(_agentId);
    }

    public void Dispose() => _factory.Dispose();

    // ── Les étapes ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_request_moves_between_open_stages()
    {
        var id = await Seed();

        var result = await Advance(id, OpportunityStage.Negotiation);

        result.IsSuccess.Should().BeTrue();

        var moved = await Load(id);
        moved.Stage.Should().Be(OpportunityStage.Negotiation);
        moved.Probability.Should().Be(0.80);
    }

    [Fact]
    public async Task A_request_moves_BACKWARDS_too()
    {
        // Une affaire qui régresse est une semaine ordinaire : interdire le retour obligerait
        // l'agent à clore et recréer, ce qui détruit l'historique que l'étape sert à tenir.
        var id = await Seed();
        await Advance(id, OpportunityStage.Negotiation);

        var result = await Advance(id, OpportunityStage.NeedsAnalysis);

        result.IsSuccess.Should().BeTrue();
        (await Load(id)).Stage.Should().Be(OpportunityStage.NeedsAnalysis);
    }

    [Theory]
    [InlineData(OpportunityStage.ClosedWon)]
    [InlineData(OpportunityStage.ClosedLost)]
    public async Task A_closed_request_refuses_every_move(OpportunityStage outcome)
    {
        var id = await Seed();
        await Close(id, outcome);

        var result = await Advance(id, OpportunityStage.Proposal);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("OPPORTUNITY_ALREADY_CLOSED");
        (await Load(id)).Stage.Should().Be(outcome);
    }

    // ── La clôture ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_writes_the_outcome_the_date_AND_the_motive()
    {
        // Le motif est la raison d'être de ce test : il était accepté puis jeté en silence, et
        // toute demande close répondait `null` à « pourquoi ».
        var id = await Seed();

        var result = await Close(id, OpportunityStage.ClosedLost, "Le client a choisi un concurrent");

        result.IsSuccess.Should().BeTrue();

        var closed = await Load(id);
        closed.Stage.Should().Be(OpportunityStage.ClosedLost);
        closed.ClosedAt.Should().Be(_clock.GetUtcNow());
        closed.CloseReason.Should().Be("Le client a choisi un concurrent");
    }

    [Fact]
    public async Task Closing_on_an_outcome_that_is_not_an_outcome_is_refused()
    {
        var id = await Seed();

        var result = await Close(id, OpportunityStage.Proposal);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("INVALID_CLOSE_OUTCOME");
        (await Load(id)).Stage.Should().Be(OpportunityStage.Prospecting);
    }

    // ── Ce que « gagnée » ouvre ────────────────────────────────────────────

    [Fact]
    public async Task Winning_opens_the_draft_contract_and_links_it_to_the_request()
    {
        var lineId = Guid.NewGuid();
        var holdingId = Guid.NewGuid();
        var id = await Seed(product: "ASSVIE");

        _administration.ResolveProductLineAsync(_tenantId, "ASSVIE", Arg.Any<CancellationToken>())
            .Returns(lineId);
        _customers.CreateHoldingFromOpportunityAsync(
                Arg.Any<CreateHoldingFromOpportunityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(holdingId));

        var result = await Close(id, OpportunityStage.ClosedWon);

        result.Value!.HoldingId.Should().Be(holdingId);

        // Ce que M01 a reçu est le lien lui-même : l'id de la demande, et l'agent qui l'a close.
        await _customers.Received(1).CreateHoldingFromOpportunityAsync(
            Arg.Is<CreateHoldingFromOpportunityRequest>(r =>
                r.TenantId == _tenantId
                && r.ClientId == _clientId
                && r.ProductLineId == lineId
                && r.SourceOpportunityId == id
                && r.ClosedByUserId == _agentId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_LOST_request_opens_nothing()
    {
        var id = await Seed();

        var result = await Close(id, OpportunityStage.ClosedLost);

        result.Value!.HoldingId.Should().BeNull();
        await _customers.DidNotReceiveWithAnyArgs()
            .CreateHoldingFromOpportunityAsync(default!, default);
    }

    [Fact]
    public async Task A_request_attached_to_no_client_opens_nothing_and_asks_nobody()
    {
        // Une demande qui ne porte qu'un lead n'a pas de fiche où ouvrir un contrat : c'est le
        // travail de la conversion. On ne doit même pas déranger M12 pour le savoir.
        var id = await SeedLeadOnly();

        var result = await Close(id, OpportunityStage.ClosedWon);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HoldingId.Should().BeNull();
        await _administration.DidNotReceiveWithAnyArgs()
            .ResolveProductLineAsync(default, default, default);
    }

    [Fact]
    public async Task An_unresolvable_product_closes_the_request_ANYWAY()
    {
        // Le produit est du texte libre : refuser la clôture ferait dépendre une décision
        // commerciale de l'orthographe d'un libellé. L'écran propose ensuite la souscription
        // à la main.
        var id = await Seed(product: "Assurance écrite à la main");

        _administration.ResolveProductLineAsync(
                _tenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var result = await Close(id, OpportunityStage.ClosedWon);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HoldingId.Should().BeNull();
        (await Load(id)).Stage.Should().Be(OpportunityStage.ClosedWon);
    }

    [Fact]
    public async Task A_refusal_from_M01_closes_the_request_ANYWAY()
    {
        var id = await Seed();

        _administration.ResolveProductLineAsync(
                _tenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        _customers.CreateHoldingFromOpportunityAsync(
                Arg.Any<CreateHoldingFromOpportunityRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<Guid>("HOLDING_ALREADY_HELD"));

        var result = await Close(id, OpportunityStage.ClosedWon);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HoldingId.Should().BeNull();
        (await Load(id)).Stage.Should().Be(OpportunityStage.ClosedWon);
    }

    [Fact]
    public async Task An_OUTAGE_closes_the_request_ANYWAY()
    {
        var id = await Seed();

        _administration.ResolveProductLineAsync(
                _tenantId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Guid?>(_ => throw new InvalidOperationException("catalogue down"));

        var result = await Close(id, OpportunityStage.ClosedWon);

        result.IsSuccess.Should().BeTrue();
        result.Value!.HoldingId.Should().BeNull();
        (await Load(id)).Stage.Should().Be(OpportunityStage.ClosedWon);
    }

    // ── Ce qui retient le gain, affiché avant le clic ──────────────────────

    [Fact]
    public async Task The_project_read_names_the_blocking_steps_that_hold_the_win()
    {
        var id = await Seed();
        var workflow = Substitute.For<Sankore.Modules.Workflow.PublicApi.IWorkflowModule>();
        workflow.GetBlockingStepsAsync(_tenantId, "Opportunity", id, Arg.Any<CancellationToken>())
            .Returns(new List<string> { "Compléter le dossier (Recette parcours)" });

        await using var db = _factory.CreateContext();
        var result = await new GetOpportunityHandler(db, workflow: workflow)
            .Handle(new GetOpportunityQuery(id), CancellationToken.None);

        result.Value!.BlockingSteps.Should().Equal("Compléter le dossier (Recette parcours)");
    }

    [Fact]
    public async Task A_closed_project_is_not_asked_for_its_blocking_steps()
    {
        var id = await Seed();
        (await Close(id, OpportunityStage.ClosedLost, "Pas intéressé")).IsSuccess.Should().BeTrue();
        var workflow = Substitute.For<Sankore.Modules.Workflow.PublicApi.IWorkflowModule>();

        await using var db = _factory.CreateContext();
        var result = await new GetOpportunityHandler(db, workflow: workflow)
            .Handle(new GetOpportunityQuery(id), CancellationToken.None);

        result.Value!.BlockingSteps.Should().BeNull();
        await workflow.DidNotReceiveWithAnyArgs().GetBlockingStepsAsync(default, default!, default, default);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private async Task<Result> Advance(Guid id, OpportunityStage stage)
    {
        await using var db = _factory.CreateContext();
        return await new AdvanceOpportunityStageHandler(db, _clock).Handle(
            new AdvanceOpportunityStageCommand(id, stage), CancellationToken.None);
    }

    private async Task<Result<CloseOpportunityResult>> Close(
        Guid id, OpportunityStage outcome, string? reason = null)
    {
        await using var db = _factory.CreateContext();
        return await new CloseOpportunityHandler(
                db, _customers, _administration, _currentUser, _clock,
                NullLogger<CloseOpportunityHandler>.Instance)
            .Handle(new CloseOpportunityCommand(id, outcome, reason), CancellationToken.None);
    }

    private async Task<Guid> Seed(string product = "ASSVIE")
    {
        await using var db = _factory.CreateContext();

        var opportunity = Opportunity.CreateForCustomer(
            tenantId:         _tenantId,
            customerEntityId: _clientId,
            title:            "Demande — " + product,
            product:          product,
            ownerId:          _agentId,
            clock:            _clock);

        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();

        return opportunity.Id;
    }

    private async Task<Guid> SeedLeadOnly()
    {
        await using var db = _factory.CreateContext();

        var opportunity = Opportunity.CreateFromLead(
            tenantId: _tenantId,
            leadId:   Guid.NewGuid(),
            title:    "Demande sans client",
            product:  "ASSVIE",
            ownerId:  _agentId,
            clock:    _clock);

        db.Opportunities.Add(opportunity);
        await db.SaveChangesAsync();

        return opportunity.Id;
    }

    private async Task<Opportunity> Load(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Opportunities.SingleAsync(o => o.Id == id);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
