namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// Covers <c>ILeadsModule.GetOpenLeadsForCustomerAsync</c> (BNF-B-22) — what the client record
/// shows so an advisor can see the requests a customer already has in flight.
/// </summary>
public sealed class GetOpenLeadsForCustomerTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    public GetOpenLeadsForCustomerTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private Lead Capture(Guid? customerId, string product = "Crédit commerçant") => Lead.Capture(
        tenantId: _tenant,
        fullName: "Aya Kouassi",
        phoneNumber: "+2250700000001",
        source: LeadSource.Agency,
        interestedProduct: product,
        preferredLanguage: "fr",
        location: new GeoPoint(5.3, -4.0),
        preferredAgencyId: null,
        clock: TimeProvider.System,
        customerId: customerId,
        customerLinkIsAutomatic: customerId.HasValue);

    private async Task SeedAsync(params Lead[] leads)
    {
        await using var db = _factory.CreateContext();
        db.Leads.AddRange(leads);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<PublicApi.CustomerOpenLead>> ReadAsync(Guid? customerId = null)
    {
        await using var db = _factory.CreateContext();
        return await new LeadsModuleFacade(db, Substitute.For<IPhoneBlindIndexer>())
            .GetOpenLeadsForCustomerAsync(_tenant, customerId ?? _customer, CancellationToken.None);
    }

    [Fact]
    public async Task Lists_a_customers_open_request()
    {
        var lead = Capture(_customer, "Épargne projet");
        await SeedAsync(lead);

        var open = await ReadAsync();

        open.Should().ContainSingle();
        open[0].LeadId.Should().Be(lead.Id);
        open[0].InterestedProduct.Should().Be("Épargne projet");
        open[0].Status.Should().Be(nameof(LeadStatus.New));
    }

    [Theory]
    [InlineData(LeadCloseReason.Lost)]
    [InlineData(LeadCloseReason.Disqualified)]
    [InlineData(LeadCloseReason.Archived)]
    public async Task A_closed_request_is_not_open(LeadCloseReason reason)
    {
        var lead = Capture(_customer);
        lead.Close(reason);
        await SeedAsync(lead);

        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_converted_request_is_not_open()
    {
        var lead = Capture(_customer);
        lead.Convert(_customer);
        await SeedAsync(lead);

        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_nurturing_request_IS_open()
    {
        // "Open" is not the same question as "dispatchable": a nurturing lead cannot be
        // dispatched and is very much a live request the client record must show.
        var lead = Capture(_customer);
        lead.Nurture();
        await SeedAsync(lead);

        (await ReadAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Another_customers_request_is_not_listed()
    {
        await SeedAsync(Capture(Guid.NewGuid()));

        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unattached_lead_is_not_listed()
    {
        await SeedAsync(Capture(customerId: null));

        (await ReadAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_customer_id_answers_empty_without_a_query()
    {
        await SeedAsync(Capture(_customer));

        (await ReadAsync(Guid.Empty)).Should().BeEmpty();
    }

    [Fact]
    public async Task Several_open_requests_come_back_newest_first()
    {
        // A customer may legitimately have several requests on different product lines.
        await SeedAsync(Capture(_customer, "Crédit"), Capture(_customer, "Épargne"));

        var open = await ReadAsync();

        open.Should().HaveCount(2);
        open.Should().BeInDescendingOrder(l => l.CapturedAt);
    }
}
