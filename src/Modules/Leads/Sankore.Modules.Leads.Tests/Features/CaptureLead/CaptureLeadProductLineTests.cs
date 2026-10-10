namespace Sankore.Modules.Leads.Tests.Features.CaptureLead;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Covers capture-time resolution of the captured product LABEL onto a catalogue line
/// (BNF-B-01, BNF-B-06).
/// </summary>
public sealed class CaptureLeadProductLineTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public CaptureLeadProductLineTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private CaptureLeadHandler Handler(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var phone = Substitute.For<IPhoneBlindIndexer>();
        phone.Compute(Arg.Any<string>()).Returns(c => $"idx_{c.Arg<string>()}");

        return new CaptureLeadHandler(
            db,
            NullLogger<CaptureLeadHandler>.Instance,
            TimeProvider.System,
            Substitute.For<IBus>(),
            phone,
            TestLeadEmailIndexer.Instance,
            Substitute.For<ICustomersModule>(),
            _admin,
            Substitute.For<IEventPublisher>());
    }

    private CaptureLeadCommand Command(
        string product = "Crédit commerçant", Guid? productLineId = null) => new(
        TenantId:          _tenant,
        FullName:          "Aya Kouassi",
        PhoneNumber:       "+2250700000001",
        Source:            LeadSource.Agency,
        InterestedProduct: product,
        PreferredLanguage: "fr",
        Latitude:          5.3,
        Longitude:         -4.0,
        PreferredAgencyId: null,
        ProductLineId:     productLineId);

    private void ResolvesTo(Guid? lineId) =>
        _admin.ResolveProductLineAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(lineId);

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.SingleAsync(l => l.Id == leadId, CancellationToken.None);
    }

    [Fact]
    public async Task A_resolved_label_lands_on_the_lead()
    {
        var line = Guid.NewGuid();
        ResolvesTo(line);

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        (await ReloadAsync(result.Value.LeadId!.Value)).ProductLineId.Should().Be(line);
    }

    [Fact]
    public async Task The_captured_label_is_kept_untouched_alongside_it()
    {
        // The two are a pair, not a replacement: what the prospect said, and what it resolved to.
        ResolvesTo(Guid.NewGuid());

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command("Crédit commerçant"), CancellationToken.None);

        (await ReloadAsync(result.Value.LeadId!.Value))
            .InterestedProduct.Should().Be("Crédit commerçant");
    }

    [Fact]
    public async Task An_unresolved_label_leaves_the_product_line_null()
    {
        // Null says exactly that: nobody has resolved it, and an agent will at enrichment.
        ResolvesTo(null);

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command("Un produit qui n'existe pas"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(result.Value.LeadId!.Value)).ProductLineId.Should().BeNull();
    }

    [Fact]
    public async Task A_caller_supplied_line_is_used_without_asking_M12()
    {
        var line = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(productLineId: line), CancellationToken.None);

        (await ReloadAsync(result.Value.LeadId!.Value)).ProductLineId.Should().Be(line);

        await _admin.DidNotReceive().ResolveProductLineAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_M12_outage_does_not_cost_the_lead()
    {
        // An unresolved product line is a lead an agent completes. A lost lead is not.
        _admin.ResolveProductLineAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<Guid?>>(_ => throw new HttpRequestException("M12 unreachable"));

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LeadId.Should().NotBeNull();
        (await ReloadAsync(result.Value.LeadId!.Value)).ProductLineId.Should().BeNull();
    }

    [Fact]
    public async Task The_captured_label_is_what_reaches_M12()
    {
        ResolvesTo(null);

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(Command("Épargne projet"), CancellationToken.None);

        await _admin.Received(1).ResolveProductLineAsync(
            _tenant, "Épargne projet", Arg.Any<CancellationToken>());
    }
}
