namespace Sankore.Modules.Customers.Tests.Features.Holdings;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Le brouillon qu'ouvre une issue commerciale — lead converti (BNF-B-24) ou demande gagnée.
///
/// <para>
/// Les deux origines partagent un seul corps, et c'est exactement ce que ces cas épinglent :
/// l'idempotence doit porter sur la BONNE des deux colonnes, et la garde
/// <c>AllowMultipleHoldings</c> doit valoir des deux côtés. Une copie par origine, c'était une
/// seconde occasion de se tromper sur la seconde — et se tromper dans le sens permissif ouvre
/// deux contrats pour une demande, ce qu'un back-office ne sait pas défaire.
/// </para>
/// </summary>
public sealed class DraftHoldingOpenerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _lineId = Guid.NewGuid();
    private readonly Guid _opportunityId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly Guid _clientId;

    public DraftHoldingOpenerTests()
    {
        _factory = new TestCustomersDbContextFactory(_tenant);
        Line();

        using var db = _factory.CreateContext();
        var client = TestClientFactory.Individual(_tenant, _agency);
        db.Clients.Add(client);
        db.SaveChanges();
        _clientId = client.Id;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_won_request_opens_a_draft_that_carries_the_request_id()
    {
        var result = await OpenFromOpportunity();

        result.IsSuccess.Should().BeTrue();

        var holding = await Load(result.Value);
        holding.Status.Should().Be(HoldingStatus.Draft);
        holding.SourceOpportunityId.Should().Be(_opportunityId);

        // Et surtout PAS sur l'autre colonne : les deux origines ne se confondent pas.
        holding.SourceLeadId.Should().BeNull();
    }

    [Fact]
    public async Task Opening_twice_for_the_same_request_returns_the_same_draft()
    {
        var first = await OpenFromOpportunity();
        var second = await OpenFromOpportunity();

        second.Value.Should().Be(first.Value);

        await using var db = _factory.CreateContext();
        var count = await db.ProductHoldings
            .CountAsync(h => h.SourceOpportunityId == _opportunityId);

        count.Should().Be(1);
    }

    [Fact]
    public async Task A_lead_and_a_request_on_the_same_line_do_NOT_deduplicate_each_other()
    {
        // L'idempotence compare la colonne de l'origine présentée. Si elle comparait les deux, la
        // demande d'un client déjà converti depuis un lead sur la même ligne se verrait refuser
        // son contrat et renvoyer celui du lead.
        var fromLead = await OpenFromLead(Guid.NewGuid());
        var fromOpportunity = await OpenFromOpportunity();

        fromOpportunity.IsSuccess.Should().BeTrue();
        fromOpportunity.Value.Should().NotBe(fromLead.Value);
    }

    [Fact]
    public async Task A_line_that_forbids_a_second_holding_refuses_the_request_too()
    {
        Line(allowMultipleHoldings: false);
        await OpenFromLead(Guid.NewGuid());

        var result = await OpenFromOpportunity();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.HoldingAlreadyHeld);
    }

    [Fact]
    public async Task An_unknown_product_line_opens_nothing()
    {
        _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
              .Returns((BeneficiaryPolicyDto?)null);

        var result = await OpenFromOpportunity();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ProductLineNotFound);
    }

    [Fact]
    public async Task An_unreachable_catalogue_is_reported_as_unavailable_not_as_absent()
    {
        // La nuance compte pour l'appelant : « ce produit n'existe pas » est définitif, « le
        // catalogue n'a pas répondu » se réessaie.
        _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
              .Returns<BeneficiaryPolicyDto?>(_ => throw new InvalidOperationException("down"));

        var result = await OpenFromOpportunity();

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ProductLineUnavailable);
    }

    [Fact]
    public async Task A_client_that_does_not_exist_opens_nothing()
    {
        await using var db = _factory.CreateContext();

        var result = await Opener(db).OpenAsync(
            DraftHoldingOrigin.FromOpportunity(
                _tenant, Guid.NewGuid(), _lineId, _opportunityId, _actor),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ── Fixture ────────────────────────────────────────────────────────────

    private void Line(bool allowMultipleHoldings = true)
        => _admin.GetBeneficiaryPolicyAsync(_tenant, _lineId, Arg.Any<CancellationToken>())
                 .Returns(TestHoldingFactory.Line(
                     productLineId: _lineId,
                     allowMultipleHoldings: allowMultipleHoldings));

    private DraftHoldingOpener Opener(CustomersDbContext db)
        => new(db, _admin, TimeProvider.System, NullLogger<DraftHoldingOpener>.Instance);

    private async Task<Result<Guid>> OpenFromOpportunity()
    {
        await using var db = _factory.CreateContext();
        return await Opener(db).OpenAsync(
            DraftHoldingOrigin.FromOpportunity(
                _tenant, _clientId, _lineId, _opportunityId, _actor),
            CancellationToken.None);
    }

    private async Task<Result<Guid>> OpenFromLead(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await Opener(db).OpenAsync(
            DraftHoldingOrigin.FromLead(_tenant, _clientId, _lineId, leadId, _actor),
            CancellationToken.None);
    }

    private async Task<ProductHolding> Load(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.ProductHoldings.SingleAsync(h => h.Id == id);
    }
}
