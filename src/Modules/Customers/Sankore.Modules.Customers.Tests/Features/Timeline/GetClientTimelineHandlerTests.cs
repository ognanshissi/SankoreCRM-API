namespace Sankore.Modules.Customers.Tests.Features.Timeline;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Timeline.GetClientTimeline;
using Sankore.Modules.Customers.Features.Timeline.Projection;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class GetClientTimelineHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private GetClientTimelineHandler BuildHandler(params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.AgencyScope(accessibleAgencies),
            TestDoubles.CurrentUser(TenantId, UserId));

    private async Task<Client> SeedClientWithFactsAsync(Guid tenantId, Guid agencyId)
    {
        var client = TimelineFixtures.Active(tenantId, agencyId, UserId);

        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);

        var now = DateTimeOffset.UtcNow;
        seed.ClientTimelineEntries.AddRange(
            TimelineFixtures.Entry(tenantId, client.Id, now.AddDays(-30),
                entryType: ClientTimelineEntryTypes.ClientCreated, summary: "Client enregistré",
                dedupKey: "k-created"),
            TimelineFixtures.Entry(tenantId, client.Id, now.AddDays(-20),
                entryType: ClientTimelineEntryTypes.ClientActivated, summary: "Client actif",
                dedupKey: "k-activated"),
            TimelineFixtures.Entry(tenantId, client.Id, now.AddDays(-10),
                sourceModule: TimelineSourceModules.Leads, entryType: "LEAD_ACTIVITY_CALL",
                summary: "Appel : Premier contact", dedupKey: "k-lead-call"),
            TimelineFixtures.Entry(tenantId, client.Id, now.AddDays(-1),
                entryType: ClientTimelineEntryTypes.SegmentChanged, summary: "Segment attribué : STANDARD",
                dedupKey: "k-segment"));

        await seed.SaveChangesAsync();
        return client;
    }

    [Fact]
    public async Task Returns_the_most_recent_fact_first()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.TotalCount.Should().Be(4);
        result.Value.Items.Select(i => i.EntryType).Should().ContainInOrder(
            ClientTimelineEntryTypes.SegmentChanged,
            "LEAD_ACTIVITY_CALL",
            ClientTimelineEntryTypes.ClientActivated,
            ClientTimelineEntryTypes.ClientCreated);
    }

    [Fact]
    public async Task Paginates_without_losing_the_total_count()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        var page2 = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id, Page: 2, PageSize: 3), CancellationToken.None);

        page2.Value.TotalCount.Should().Be(4);
        page2.Value.Items.Should().HaveCount(1);
        page2.Value.Items[0].EntryType.Should().Be(ClientTimelineEntryTypes.ClientCreated);
    }

    [Fact]
    public async Task Clamps_a_page_number_below_one()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id, Page: -5, PageSize: 2), CancellationToken.None);

        result.Value.Page.Should().Be(1);
        result.Value.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Filters_by_source_module()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id, SourceModule: TimelineSourceModules.Leads),
            CancellationToken.None);

        result.Value.TotalCount.Should().Be(1);
        result.Value.Items.Should().ContainSingle()
            .Which.EntryType.Should().Be("LEAD_ACTIVITY_CALL");
    }

    [Fact]
    public async Task Filters_by_entry_type()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id, EntryType: ClientTimelineEntryTypes.ClientActivated),
            CancellationToken.None);

        result.Value.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Answers_client_not_found_when_the_client_is_outside_the_agency_perimeter()
    {
        var client = await SeedClientWithFactsAsync(TenantId, OtherAgencyId);

        // The caller only sees AgencyId, so the client's agency is out of reach.
        var result = await BuildHandler(AgencyId).Handle(
            new GetClientTimelineQuery(client.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound,
            "a 403 would confirm the record exists — out of perimeter must be indistinguishable from unknown");
    }

    [Fact]
    public async Task Answers_client_not_found_for_an_unknown_client()
    {
        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Never_returns_the_timeline_of_a_client_of_another_tenant()
    {
        var foreignClient = await SeedClientWithFactsAsync(OtherTenantId, AgencyId);

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(foreignClient.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Never_mixes_the_facts_of_two_tenants_sharing_a_client_id()
    {
        var client = await SeedClientWithFactsAsync(TenantId, AgencyId);

        await using (var seed = _factory.CreateContext())
        {
            // Same client id, other tenant: four more rows that must stay invisible.
            seed.ClientTimelineEntries.AddRange(
                TimelineFixtures.Entry(OtherTenantId, client.Id, DateTimeOffset.UtcNow,
                    summary: "Fait d'un autre tenant", dedupKey: "foreign-1"),
                TimelineFixtures.Entry(OtherTenantId, client.Id, DateTimeOffset.UtcNow.AddDays(-2),
                    summary: "Fait d'un autre tenant", dedupKey: "foreign-2"));
            await seed.SaveChangesAsync();
        }

        var result = await BuildHandler().Handle(
            new GetClientTimelineQuery(client.Id), CancellationToken.None);

        result.Value.TotalCount.Should().Be(4);
        result.Value.Items.Should().NotContain(i => i.Summary.Contains("autre tenant"));
    }
}
