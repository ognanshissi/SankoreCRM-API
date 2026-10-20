namespace Sankore.Modules.Leads.Tests.Features.Opportunities;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.UpdateOpportunity;
using Sankore.Modules.Leads.Tests.TestSupport;
using Xunit;

/// <summary>
/// Ce que l'onglet Opportunités du lead écrit en plus des étapes : la prochaine action, le produit
/// une fois connu, et la réponse à « deux demandes différentes ? ».
/// </summary>
public sealed class OpportunityFollowUpTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T09:00:00Z");

    public OpportunityFollowUpTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private async Task<Guid> SeedAsync(string? product = "Crédit équipement")
    {
        var opp = Opportunity.CreateFromLead(
            _tenantId, Guid.NewGuid(), "Crédit pour un chantier à Daloa", product,
            ownerId: null, TimeProvider.System);
        await using var db = _factory.CreateContext();
        db.Opportunities.Add(opp);
        await db.SaveChangesAsync(CancellationToken.None);
        return opp.Id;
    }

    private async Task UpdateAsync(UpdateOpportunityCommand cmd)
    {
        await using var db = _factory.CreateContext();
        var result = await new UpdateOpportunityHandler(db, new FixedTime(Now)).Handle(cmd, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
    }

    private async Task<Opportunity> LoadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Opportunities.FirstAsync(o => o.Id == id, CancellationToken.None);
    }

    [Fact]
    public async Task A_need_is_noted_before_anybody_matched_it_to_a_product()
    {
        var id = await SeedAsync(product: null);

        (await LoadAsync(id)).Product.Should().BeEmpty();
        new CreateOpportunityValidator()
            .Validate(new CreateOpportunityCommand(_tenantId, Guid.NewGuid(), "Chantier", Product: null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task The_product_can_be_set_afterwards()
    {
        var id = await SeedAsync(product: null);

        await UpdateAsync(new UpdateOpportunityCommand(id, Product: "Crédit habitat"));

        (await LoadAsync(id)).Product.Should().Be("Crédit habitat");
    }

    [Fact]
    public async Task The_next_step_is_set_with_its_date_and_cleared_with_it()
    {
        var id = await SeedAsync();

        await UpdateAsync(new UpdateOpportunityCommand(
            id, NextStep: " Planifier la visite du chantier ", NextStepDueOn: new DateOnly(2026, 10, 27)));
        var stored = await LoadAsync(id);
        stored.NextStep.Should().Be("Planifier la visite du chantier");
        stored.NextStepDueOn.Should().Be(new DateOnly(2026, 10, 27));

        // Null leaves alone: changing the title does not erase the next step.
        await UpdateAsync(new UpdateOpportunityCommand(id, Title: "Chantier à Daloa"));
        (await LoadAsync(id)).NextStep.Should().Be("Planifier la visite du chantier");

        await UpdateAsync(new UpdateOpportunityCommand(id, NextStep: ""));
        stored = await LoadAsync(id);
        stored.NextStep.Should().BeNull();
        stored.NextStepDueOn.Should().BeNull("a date without its action means nothing");
    }

    [Fact]
    public async Task Confirming_two_different_needs_is_recorded()
    {
        var id = await SeedAsync();

        await UpdateAsync(new UpdateOpportunityCommand(id, ConfirmSeparateNeed: true));

        (await LoadAsync(id)).SeparateNeedConfirmedAt.Should().Be(Now);
    }

    [Fact]
    public void An_over_long_next_step_is_refused_by_the_validator()
    {
        new UpdateOpportunityValidator()
            .Validate(new UpdateOpportunityCommand(Guid.NewGuid(), NextStep: new string('x', 201)))
            .IsValid.Should().BeFalse();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
