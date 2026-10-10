namespace Sankore.Modules.Leads.Tests.Features.CustomerLink;

using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Covers capture-time recognition of an existing customer (BNF-B-22) — including the two cases
/// the specification is most explicit about: never a silent attachment, and never a lost lead.
/// </summary>
public sealed class CaptureLeadCustomerMatchTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();

    public CaptureLeadCustomerMatchTests() => _factory = new TestDbContextFactory(_tenant);

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
            _customers,
            // No product line configured: ResolveProductLineAsync returns null by default under
            // NSubstitute, so these leads carry none — which is what they assert on.
            Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>(),
            Substitute.For<IEventPublisher>());
    }

    private CaptureLeadCommand Command(Guid? customerId = null) => new(
        TenantId:          _tenant,
        FullName:          "Aya Kouassi",
        PhoneNumber:       "+2250700000001",
        Source:            LeadSource.Agency,
        InterestedProduct: "Crédit commerçant",
        PreferredLanguage: "fr",
        Latitude:          5.3,
        Longitude:         -4.0,
        PreferredAgencyId: null,
        Email:             "aya@example.ci",
        NationalId:        "CI12345642",
        CustomerId:        customerId);

    private void MatchesReturn(params CustomerMatch[] matches) =>
        _customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(matches);

    private static CustomerMatch Match(Guid clientId, params string[] matchedOn) => new(
        clientId, "CLI-0001", "Aya Kouassi", Guid.NewGuid(), null, "Active", "Approved", matchedOn);

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.SingleAsync(l => l.Id == leadId, CancellationToken.None);
    }

    // ── Automatic attachment ──────────────────────────────────────────────

    [Fact]
    public async Task A_unique_document_match_is_attached_automatically()
    {
        var client = Guid.NewGuid();
        MatchesReturn(Match(client, "identityDocument"));

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.LinkedCustomerId.Should().Be(client);

        var lead = await ReloadAsync(result.Value.LeadId!.Value);
        lead.CustomerId.Should().Be(client);
        lead.Kind.Should().Be(LeadKind.ExistingCustomer);
        lead.CustomerLinkIsAutomatic.Should().BeTrue();
        lead.CustomerLinkReason.Should().NotBeNullOrWhiteSpace("an automatic link still says why");
    }

    // ── Never a silent attachment ─────────────────────────────────────────

    [Fact]
    public async Task A_phone_only_match_is_captured_unattached()
    {
        // A shared household handset. The lead is created, the candidate is left for an agent to
        // confirm through GET leads/{id}/customer-matches.
        MatchesReturn(Match(Guid.NewGuid(), "phone"));

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.Value.LinkedCustomerId.Should().BeNull();
        (await ReloadAsync(result.Value.LeadId!.Value)).CustomerId.Should().BeNull();
    }

    [Fact]
    public async Task Several_candidates_leave_the_lead_unattached()
    {
        MatchesReturn(
            Match(Guid.NewGuid(), "identityDocument"),
            Match(Guid.NewGuid(), "email"));

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.Value.LinkedCustomerId.Should().BeNull(
            "BNF-B-22 forbids choosing between candidates without an agent");
    }

    [Fact]
    public async Task No_candidate_leaves_the_lead_unattached()
    {
        MatchesReturn();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (await ReloadAsync(result.Value.LeadId!.Value)).Kind.Should().Be(LeadKind.NewProspect);
    }

    // ── Never a lost lead ─────────────────────────────────────────────────

    [Fact]
    public async Task An_M01_outage_does_not_cost_the_lead()
    {
        // The thing being captured is a prospect, often with somebody on the phone. Recognising
        // them is an enrichment of that, not a precondition — so the lookup is best-effort.
        _customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CustomerMatch>>>(_ =>
                throw new HttpRequestException("M01 unreachable"));

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("losing the lead would be far worse than not matching it");
        result.Value.LeadId.Should().NotBeNull();
        result.Value.LinkedCustomerId.Should().BeNull();
    }

    // ── A caller that already knows ───────────────────────────────────────

    [Fact]
    public async Task An_explicit_customer_id_is_used_without_asking_M01()
    {
        // A counter clerk opening a second product for the client in front of them.
        var client = Guid.NewGuid();

        await using var db = _factory.CreateContext();
        var result = await Handler(db).Handle(Command(customerId: client), CancellationToken.None);

        result.Value.LinkedCustomerId.Should().Be(client);

        await _customers.DidNotReceive().FindMatchesAsync(
            Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        var lead = await ReloadAsync(result.Value.LeadId!.Value);
        lead.CustomerLinkIsAutomatic.Should().BeFalse("a caller naming the client is not the matcher");
    }

    [Fact]
    public async Task The_clear_email_and_document_are_what_reach_M01()
    {
        // M01 holds a different blind-index key, so it has to index the raw values itself — the
        // caller could not have produced an index M01 can match.
        MatchesReturn();

        await using var db = _factory.CreateContext();
        await Handler(db).Handle(Command(), CancellationToken.None);

        await _customers.Received(1).FindMatchesAsync(
            _tenant, "+2250700000001", "aya@example.ci", "CI12345642", Arg.Any<CancellationToken>());
    }
}
