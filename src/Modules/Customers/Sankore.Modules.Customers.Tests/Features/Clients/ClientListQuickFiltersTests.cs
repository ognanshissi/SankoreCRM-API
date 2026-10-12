namespace Sankore.Modules.Customers.Tests.Features.Clients;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.SearchClients;
using Sankore.Modules.Customers.Features.Lifecycle.Consumers;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.Features.Lifecycle;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public sealed class ClientListQuickFiltersTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ActorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AdvisorId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Quatre clients : sans conseiller et KYC non commencé ; KYC en cours ; risque élevé avec
    /// conseiller ; validé avec conseiller.
    /// </summary>
    private async Task<Client[]> SeedAsync()
    {
        var notStarted = TestClientFactory.Individual(TenantId, AgencyId, "Aboubacar", "Toure", "ABJ-2026-000001");
        var inProgress = TestClientFactory.Individual(TenantId, AgencyId, "Adama", "Diallo", "ABJ-2026-000002", advisorUserId: AdvisorId);
        inProgress.ApplyKycInitiated(DateTimeOffset.UtcNow);
        var highRisk = TestClientFactory.Individual(TenantId, AgencyId, "Ivoire", "Negoce", "ABJ-2026-000003", advisorUserId: AdvisorId);
        highRisk.ApplyRiskLevel(RiskLevel.High, DateTimeOffset.UtcNow);
        var validated = TestClientFactory.Individual(TenantId, AgencyId, "Adjoua", "Diallo", "ABJ-2026-000004", advisorUserId: AdvisorId);
        validated.ApplyKycValidated(DateTimeOffset.UtcNow, ActorId);

        await using var db = _factory.CreateContext();
        db.Clients.AddRange(notStarted, inProgress, highRisk, validated);
        await db.SaveChangesAsync();
        return [notStarted, inProgress, highRisk, validated];
    }

    private static SearchClientsQuery Query(ClientQuickFilter quick = ClientQuickFilter.All) => new(
        null, null, null, null, null, null, null, null, null, 1, 20, quick);

    private SearchClientsHandler Search(CustomersDbContext db, IAdministrationModule? admin = null, IKycModule? kyc = null) => new(
        db, TestDoubles.CurrentUser(TenantId, ActorId), TestDoubles.AgencyScope(), TestDoubles.Encryptor(),
        TestDoubles.Indexer(), admin, kyc);

    [Theory]
    [InlineData(ClientQuickFilter.KycToStart, 2)]   // l'élevé n'a pas non plus commencé
    [InlineData(ClientQuickFilter.KycInProgress, 1)]
    [InlineData(ClientQuickFilter.HighRisk, 1)]
    [InlineData(ClientQuickFilter.WithoutAdvisor, 1)]
    [InlineData(ClientQuickFilter.All, 4)]
    public async Task Each_shortcut_lists_exactly_what_its_counter_counts(ClientQuickFilter quick, int expected)
    {
        await SeedAsync();

        await using var db = _factory.CreateContext();
        var page = await Search(db).Handle(Query(quick), CancellationToken.None);

        await using var countsDb = _factory.CreateContext();
        var counts = (await new GetClientQuickCountsHandler(
                countsDb, TestDoubles.CurrentUser(TenantId, ActorId), TestDoubles.AgencyScope(), TestDoubles.Indexer())
            .Handle(new GetClientQuickCountsQuery(Query().Criteria), CancellationToken.None)).Value;

        page.Value.TotalCount.Should().Be(expected);
        var counted = quick switch
        {
            ClientQuickFilter.KycToStart => counts.KycToStart,
            ClientQuickFilter.KycInProgress => counts.KycInProgress,
            ClientQuickFilter.HighRisk => counts.HighRisk,
            ClientQuickFilter.WithoutAdvisor => counts.WithoutAdvisor,
            _ => counts.All,
        };
        counted.Should().Be(expected);
    }

    [Fact]
    public async Task Rows_carry_names_products_and_the_kyc_journey_from_the_other_modules()
    {
        var clients = await SeedAsync();
        var adama = clients[1];

        var admin = Substitute.For<IAdministrationModule>();
        admin.GetAgencyAsync(TenantId, AgencyId, Arg.Any<CancellationToken>())
            .Returns(new AgencySummary(AgencyId, "PLT", "Agence Plateau", null, true));
        admin.GetUserNamesAsync(TenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [AdvisorId] = "Ibrahim Sanogo" });

        var kyc = Substitute.For<IKycModule>();
        var fileId = Guid.NewGuid();
        kyc.GetJourneySummariesAsync(TenantId, Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, KycJourneySummary>
            {
                [adama.Id] = new(adama.Id, fileId, "Verifying", 2, true, true, false, false, 87),
            });

        await using var db = _factory.CreateContext();
        var page = await Search(db, admin, kyc).Handle(Query(), CancellationToken.None);

        var row = page.Value.Items.Single(i => i.Id == adama.Id);
        row.AgencyName.Should().Be("Agence Plateau");
        row.AdvisorName.Should().Be("Ibrahim Sanogo");
        row.Kyc!.Stage.Should().Be("Verifying");
        row.Kyc.ConfidenceScore.Should().Be(87);
        row.Products.Should().BeEmpty();
        page.Value.Items.Single(i => i.Id == clients[0].Id).Kyc.Should().BeNull("aucun dossier");
    }

    [Fact]
    public async Task A_failing_module_empties_its_column_not_the_list()
    {
        await SeedAsync();
        var kyc = Substitute.For<IKycModule>();
        kyc.GetJourneySummariesAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<Guid, KycJourneySummary>>(_ => throw new InvalidOperationException("KYC down"));

        await using var db = _factory.CreateContext();
        var page = await Search(db, kyc: kyc).Handle(Query(), CancellationToken.None);

        page.Value.Items.Should().HaveCount(4).And.OnlyContain(i => i.Kyc == null);
    }

    [Fact]
    public async Task Opening_a_kyc_file_moves_the_client_to_in_progress_once_and_never_unvalidates()
    {
        var clients = await SeedAsync();
        var inbox = new FakeInboxGuard();

        async Task Consume(Client c)
        {
            var evt = new KycInitiatedEvent(TenantId, c.Id, Guid.NewGuid(), "Agency", ActorId);
            var context = Substitute.For<ConsumeContext<KycInitiatedEvent>>();
            context.Message.Returns(evt);
            context.CancellationToken.Returns(CancellationToken.None);
            await new KycInitiatedConsumer(_factory.CreateContext(), inbox, NullLogger<KycInitiatedConsumer>.Instance)
                .Consume(context);
        }

        await Consume(clients[0]);
        await Consume(clients[3]);

        await using var db = _factory.CreateContext();
        (await db.Clients.SingleAsync(c => c.Id == clients[0].Id)).KycStatus.Should().Be(KycStatus.InProgress);
        (await db.Clients.SingleAsync(c => c.Id == clients[3].Id)).KycStatus.Should().Be(KycStatus.Approved);
    }
}
