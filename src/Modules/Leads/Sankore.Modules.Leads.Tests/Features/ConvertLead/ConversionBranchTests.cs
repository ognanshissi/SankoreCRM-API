namespace Sankore.Modules.Leads.Tests.Features.ConvertLead;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Features.ConvertLead.Events;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>
/// The two conversion paths of BNF-B-12, and what BNF-B-24 puts on the events.
/// </summary>
public sealed class ConversionBranchTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _agency = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly Guid _line = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly IKycModule _kyc = Substitute.For<IKycModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ConversionBranchTests()
    {
        _factory = new TestDbContextFactory(_tenant);

        // An ATTACHED lead's completeness reads the client file (BNF-B-23), and an unreachable or
        // unapproved one blocks the conversion — correctly. These tests are about the branching,
        // so the file is in order; ExistingCustomerCompletenessTests covers when it is not.
        _customers.GetClientSummaryAsync(_tenant, _customer, Arg.Any<CancellationToken>())
                  .Returns(new ClientSummary(
                      _customer, "ABJ-PLT-2026-000042", "Individual", "Aya Kouassi",
                      "Active", _agency, null, "Approved", "Low", null));

        _kyc.GetStatusAsync(_tenant, _customer, Arg.Any<CancellationToken>())
            .Returns(KycStatus.Approved);
    }

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Lead> SeedAsync(
        bool attached = false, bool exposed = false, Guid? lineId = null)
    {
        var lead = Lead.Capture(
            tenantId: _tenant, fullName: "Aya Kouassi", phoneNumber: "+2250700000001",
            source: LeadSource.Agency, interestedProduct: "Assurance santé",
            preferredLanguage: "fr", location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: _agency, clock: TimeProvider.System,
            firstName: "Aya", lastName: "Kouassi", ownerId: _agent, productLineId: lineId);

        if (attached)
            lead.AttachToCustomer(_customer, isAutomatic: false, reason: "Confirmed by the agent");

        if (exposed)
        {
            lead.SetPrincipalPepStatus(
                PepStatus.Exposed("Députée", PepLink.Self, "CI"),
                DateTimeOffset.UtcNow, postMandateMonths: 12);
        }

        await using var db = _factory.CreateContext();
        db.Leads.Add(lead);
        await db.SaveChangesAsync(CancellationToken.None);
        return lead;
    }

    private ConvertLeadHandler Handler(Sankore.Modules.Leads.Infrastructure.LeadsDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Id.Returns(_agent);
        user.TenantId.Returns(_tenant);

        return new ConvertLeadHandler(
            db, user, _customers, Substitute.For<Sankore.Modules.Administration.PublicApi.IAdministrationModule>(), TestLeadEmailIndexer.Instance,
            TestCompleteness.Calculator(db, customers: _customers, kyc: _kyc), _publisher);
    }

    private async Task<Result<ConvertLeadResult>> ConvertAsync(ConvertLeadCommand cmd)
    {
        await using var db = _factory.CreateContext();
        return await Handler(db).Handle(cmd, CancellationToken.None);
    }

    private void M01Creates(Guid clientId)
        => _customers.CreateFromLeadAsync(Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>())
                     .Returns(Result.Ok(new CreateFromLeadResult(clientId, "N-1", false)));

    private T Published<T>() => _publisher.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<T>().Single();

    // ── the existing-customer path (BNF-B-12, BNF-B-22) ─────────────────────

    [Fact]
    public async Task An_attached_lead_converts_onto_its_client_without_creating_one()
    {
        // "Sans ressaisie": the client already exists, so M01 is not asked to create anything and
        // nothing of the identity travels again.
        var lead = await SeedAsync(attached: true);

        var result = await ConvertAsync(new ConvertLeadCommand(lead.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.CustomerId.Should().Be(_customer);
        result.Value.ConversionType.Should().Be(LeadConversionType.ExistingCustomerNewProduct);

        await _customers.DidNotReceive().CreateFromLeadAsync(
            Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_new_prospect_still_goes_through_M01()
    {
        var clientId = Guid.NewGuid();
        M01Creates(clientId);
        var lead = await SeedAsync();

        var result = await ConvertAsync(new ConvertLeadCommand(lead.Id));

        result.Value.CustomerId.Should().Be(clientId);
        result.Value.ConversionType.Should().Be(LeadConversionType.NewCustomer);
    }

    [Fact]
    public async Task The_duplicate_gate_does_not_block_a_known_customers_second_request()
    {
        // Other open leads for the same person are the EXPECTED state for a customer asking for a
        // second product — the client record lists them as requests in progress. Blocking on them
        // would make a second subscription impossible.
        var first = await SeedAsync(attached: true);
        var second = await SeedAsync(attached: true);

        // Force: false — the gate is reached and must decline to look.
        var result = await ConvertAsync(new ConvertLeadCommand(second.Id, Force: false));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.DuplicateDetected.Should().BeFalse();
        first.Id.Should().NotBe(second.Id);
    }

    [Fact]
    public async Task A_new_prospect_with_a_twin_is_still_blocked()
    {
        // The contrast: the gate still does its job on the path it was written for.
        await SeedAsync();
        var second = await SeedAsync();

        var result = await ConvertAsync(new ConvertLeadCommand(second.Id, Force: false));

        result.Value.DuplicateDetected.Should().BeTrue();
    }

    [Fact]
    public async Task Naming_a_different_client_than_the_lead_is_attached_to_is_a_conflict()
    {
        var lead = await SeedAsync(attached: true);
        _customers.ExistsAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await ConvertAsync(
            new ConvertLeadCommand(lead.Id, CustomerId: Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_LINK_CONFLICT",
            "one of the two records is wrong, and picking either converts against an unreviewed file");
    }

    // ── idempotence (BNF-B-24) ──────────────────────────────────────────────

    [Fact]
    public async Task Converting_twice_answers_the_same_client_and_publishes_nothing_again()
    {
        var clientId = Guid.NewGuid();
        M01Creates(clientId);
        var lead = await SeedAsync();

        var first = await ConvertAsync(new ConvertLeadCommand(lead.Id));
        _publisher.ClearReceivedCalls();

        var second = await ConvertAsync(new ConvertLeadCommand(lead.Id));

        second.IsSuccess.Should().BeTrue(second.Error);
        second.Value.CustomerId.Should().Be(first.Value.CustomerId);
        second.Value.AlreadyConverted.Should().BeTrue();

        _publisher.ReceivedCalls().Should().BeEmpty(
            "a back office must not be told twice to open the same holding");
    }

    [Fact]
    public async Task An_open_client_file_cannot_be_re_pointed_at_another_client()
    {
        M01Creates(Guid.NewGuid());
        var lead = await SeedAsync();
        await ConvertAsync(new ConvertLeadCommand(lead.Id));

        var result = await ConvertAsync(
            new ConvertLeadCommand(lead.Id, CustomerId: Guid.NewGuid()));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_LINK_CONFLICT");
    }

    // ── what the events carry (BNF-B-24) ────────────────────────────────────

    [Fact]
    public async Task KycRequested_carries_the_PEP_exposure_flag()
    {
        M01Creates(Guid.NewGuid());
        var lead = await SeedAsync(exposed: true);

        await ConvertAsync(new ConvertLeadCommand(lead.Id));

        Published<KycRequestedIntegrationEvent>().HasPepExposure.Should().BeTrue(
            "it is what opens the KYC file at High vigilance and adds the compliance officer");
    }

    [Fact]
    public async Task And_carries_the_flag_only_the_flag()
    {
        M01Creates(Guid.NewGuid());
        var lead = await SeedAsync(exposed: true);

        await ConvertAsync(new ConvertLeadCommand(lead.Id));

        // The function, the country and the family link stay behind lead:pep:read in M13. An
        // integration event is the wrong place to replicate a compliance classification.
        System.Text.Json.JsonSerializer.Serialize(Published<KycRequestedIntegrationEvent>())
            .Should().NotContain("Députée");
    }

    // ── no draft holding at the file opening ────────────────────────────────

    [Fact]
    public async Task Opening_the_client_file_opens_no_draft_holding()
    {
        // The contract is opened when the PROJECT is won, on the project's own product.
        M01Creates(Guid.NewGuid());
        var lead = await SeedAsync(lineId: _line);

        var result = await ConvertAsync(new ConvertLeadCommand(lead.Id));

        result.IsSuccess.Should().BeTrue(result.Error);
        result.Value.HoldingId.Should().BeNull();
        await _customers.DidNotReceive().CreateHoldingFromLeadAsync(
            Arg.Any<CreateHoldingFromLeadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unexposed_lead_opens_an_ordinary_file()
    {
        M01Creates(Guid.NewGuid());
        var lead = await SeedAsync();

        await ConvertAsync(new ConvertLeadCommand(lead.Id));

        Published<KycRequestedIntegrationEvent>().HasPepExposure.Should().BeFalse();
    }
}
