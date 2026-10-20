namespace Sankore.Modules.Leads.Tests.Features.Household;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Features.Household.SetHouseholdResources;
using Sankore.Modules.Leads.Features.Questionnaires;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Tests.Features.Enrichment;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The household's monthly resources: declared together, replaced together, and only by somebody
/// who may work the lead.
/// </summary>
public sealed class SetHouseholdResourcesHandlerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public SetHouseholdResourcesHandlerTests()
    {
        _factory = new TestDbContextFactory(_tenant);
        _admin.GetComplianceSettingsAsync(_tenant, Arg.Any<CancellationToken>())
              .Returns(TestCompleteness.FatcaOff);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<Lead> SeedAsync(Guid? ownerId = null)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Didier Gbagbo", phoneNumber: "+2250701020324",
            source: LeadSource.CallCenter, interestedProduct: "Crédit équipement",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null, clock: TimeProvider.System,
            ownerId: ownerId ?? _agent);

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);
        return lead;
    }

    private async Task<Result> SendAsync(SetHouseholdResourcesCommand cmd)
    {
        await using var db = _factory.CreateContext();
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenant);
        user.Roles.Returns([Roles.CommercialAgent.Code]);

        var context = new HouseholdWriteContext(
            db, new LeadAccessGuard(user, _admin), new HouseholdPolicyResolver(_admin), _admin,
            new ExpectedResponsePlanner(db, _admin, TimeProvider.System), _publisher,
            new FixedClock(Now), NullLogger<HouseholdWriteContext>.Instance);

        return await new SetHouseholdResourcesHandler(context).Handle(cmd, CancellationToken.None);
    }

    private async Task<Lead> ReloadAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.FirstAsync(l => l.Id == id, CancellationToken.None);
    }

    [Fact]
    public async Task It_records_the_three_figures_with_the_currency_and_the_date()
    {
        var lead = await SeedAsync();

        var result = await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 450_000m, 120_000m, 60_000m));

        result.IsSuccess.Should().BeTrue(result.Error);
        var stored = await ReloadAsync(lead.Id);
        stored.HouseholdMonthlyIncome.Should().Be(450_000m);
        stored.HouseholdHousingCost.Should().Be(120_000m);
        stored.HouseholdOtherDebts.Should().Be(60_000m);
        stored.HouseholdResourcesCurrency.Should().Be("XOF", "the network's currency when none is given");
        stored.HouseholdResourcesDeclaredAt.Should().Be(Now);
    }

    [Fact]
    public async Task A_new_declaration_replaces_the_previous_one_entirely()
    {
        // Read as a ratio: last month's rent left next to this month's income would be a wrong one.
        var lead = await SeedAsync();
        await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 450_000m, 120_000m, 60_000m));

        await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 500_000m, null, null));

        var stored = await ReloadAsync(lead.Id);
        stored.HouseholdMonthlyIncome.Should().Be(500_000m);
        stored.HouseholdHousingCost.Should().BeNull();
        stored.HouseholdOtherDebts.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_every_figure_clears_the_declaration()
    {
        var lead = await SeedAsync();
        await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 450_000m, null, null));

        await SendAsync(new SetHouseholdResourcesCommand(lead.Id, null, null, null));

        var stored = await ReloadAsync(lead.Id);
        stored.HouseholdResourcesDeclaredAt.Should().BeNull();
        stored.HouseholdResourcesCurrency.Should().BeNull();
    }

    [Fact]
    public async Task A_negative_amount_is_refused_by_the_aggregate()
    {
        var lead = await SeedAsync();

        var result = await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 450_000m, -1m, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_RESOURCES_NEGATIVE");
    }

    [Fact]
    public async Task Another_agents_lead_is_refused()
    {
        var lead = await SeedAsync(ownerId: Guid.NewGuid());

        var result = await SendAsync(new SetHouseholdResourcesCommand(lead.Id, 450_000m, null, null));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(LeadAccessGuard.Forbidden);
    }
}
