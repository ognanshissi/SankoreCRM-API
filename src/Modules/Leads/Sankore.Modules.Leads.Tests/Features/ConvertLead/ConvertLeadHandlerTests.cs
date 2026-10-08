namespace Sankore.Modules.Leads.Tests.Features.ConvertLead;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Customer360.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ConvertLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Converting a lead has to END with a client that exists. Before this was wired, the handler
/// minted a Guid, stamped it on the lead and published an event nobody consumed — the lead came
/// out "Converted" pointing at a customer id that matched no row anywhere.
/// </summary>
public sealed class ConvertLeadHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;

    private readonly ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private readonly ICustomerModule _legacyCustomers = Substitute.For<ICustomerModule>();
    private readonly IEventPublisher _publisher = Substitute.For<IEventPublisher>();

    public ConvertLeadHandlerTests() => _factory = new TestDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    // ── fixtures ────────────────────────────────────────────────────────────

    private async Task<Lead> SeedLeadAsync()
    {
        await using var db = _factory.CreateContext();
        var lead = LeadTestBuilder.Create()
            .WithTenant(_tenantId)
            .WithAgency(_agencyId)
            .Build();

        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        return lead;
    }

    private ConvertLeadHandler Handler()
    {
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(_userId);
        currentUser.TenantId.Returns(_tenantId);

        return new ConvertLeadHandler(
            _factory.CreateContext(), currentUser, _customers, _publisher);
    }

    private void CustomersReturn(CreateFromLeadResult result) =>
        _customers.CreateFromLeadAsync(Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(result));

    private async Task<Lead> ReloadAsync(Guid leadId)
    {
        await using var db = _factory.CreateContext();
        return db.Leads.Single(l => l.Id == leadId);
    }

    // ── the point of the whole feature ──────────────────────────────────────

    [Fact]
    public async Task Creates_the_client_and_stamps_its_real_id_on_the_lead()
    {
        var lead = await SeedLeadAsync();
        var clientId = Guid.NewGuid();
        CustomersReturn(new CreateFromLeadResult(clientId, "ABJ-PLT-2026-000042", AlreadyExisted: false));

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerId.Should().Be(clientId, "the id must come from M01, not be invented");

        var reloaded = await ReloadAsync(lead.Id);
        reloaded.Status.Should().Be(LeadStatus.Converted);
        reloaded.ConvertedToCustomerId.Should().Be(clientId);
    }

    [Fact]
    public async Task Pre_fills_the_client_with_what_the_lead_already_collected()
    {
        var lead = await SeedLeadAsync();
        CustomersReturn(new CreateFromLeadResult(Guid.NewGuid(), "N-1", AlreadyExisted: false));

        await Handler().Handle(new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        var request = _customers.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<CreateFromLeadRequest>()
            .Single();

        request.TenantId.Should().Be(_tenantId);
        request.LeadId.Should().Be(lead.Id, "this is what makes the call idempotent");
        request.AgencyId.Should().Be(_agencyId);
        request.ConvertedByUserId.Should().Be(_userId);
        request.PhoneNumber.Should().Be(lead.PhoneNumber);
        request.PreferredLanguage.Should().Be(lead.PreferredLanguage);
    }

    [Fact]
    public async Task Both_downstream_events_carry_the_real_client_id()
    {
        // KycRequested used to be published with the invented id, so M02 would have opened a
        // KYC file against a customer that did not exist.
        var lead = await SeedLeadAsync();
        var clientId = Guid.NewGuid();
        CustomersReturn(new CreateFromLeadResult(clientId, "N-1", AlreadyExisted: false));

        await Handler().Handle(new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        var published = _publisher.ReceivedCalls().Select(c => c.GetArguments()[0]).ToList();

        published.OfType<Leads.Features.ConvertLead.Events.LeadConvertedIntegrationEvent>()
            .Single().CustomerId.Should().Be(clientId);
        published.OfType<Sankore.Modules.Kyc.PublicApi.KycRequestedIntegrationEvent>()
            .Single().CustomerEntityId.Should().Be(clientId);
    }

    // ── idempotence and refusals ────────────────────────────────────────────

    [Fact]
    public async Task A_lead_already_converted_reuses_the_same_client()
    {
        var lead = await SeedLeadAsync();
        var clientId = Guid.NewGuid();
        CustomersReturn(new CreateFromLeadResult(clientId, "N-1", AlreadyExisted: true));

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerId.Should().Be(clientId,
            "M01 is idempotent on SourceLeadId — a retry must not create a second client");
    }

    [Fact]
    public async Task A_blocking_duplicate_leaves_the_lead_unconverted_and_hands_back_the_existing_client()
    {
        var lead = await SeedLeadAsync();
        var existingId = Guid.NewGuid();
        CustomersReturn(new CreateFromLeadResult(
            existingId, "ABJ-PLT-2026-000001", AlreadyExisted: false,
            BlockingCode: "DUPLICATE_IDENTITY_DOCUMENT"));

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.BlockingCode.Should().Be("DUPLICATE_IDENTITY_DOCUMENT");
        result.Value.ExistingCustomerId.Should().Be(existingId);
        result.Value.ExistingCustomerNumber.Should().Be("ABJ-PLT-2026-000001");

        (await ReloadAsync(lead.Id)).Status.Should().NotBe(LeadStatus.Converted,
            "the agent has to attach it by hand; marking it converted would hide the conflict");

        await _publisher.DidNotReceive().PublishAsync(
            Arg.Any<Sankore.Modules.Kyc.PublicApi.KycRequestedIntegrationEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failure_from_the_customers_module_leaves_the_lead_alone()
    {
        var lead = await SeedLeadAsync();
        _customers.CreateFromLeadAsync(Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail<CreateFromLeadResult>("AGENCY_OUT_OF_SCOPE"));

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("AGENCY_OUT_OF_SCOPE");
        (await ReloadAsync(lead.Id)).Status.Should().NotBe(LeadStatus.Converted);
    }

    [Fact]
    public async Task A_lead_routed_to_no_agency_cannot_become_a_client()
    {
        // A client belongs to an agency; nobody can choose one on the lead's behalf here.
        Lead lead;
        await using (var db = _factory.CreateContext())
        {
            lead = LeadTestBuilder.Create().WithTenant(_tenantId).Build();
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, Force: true), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_HAS_NO_AGENCY");

        await _customers.DidNotReceive().CreateFromLeadAsync(
            Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>());
    }

    // ── attaching to a customer the caller already chose ─────────────────────

    [Fact]
    public async Task An_existing_customer_is_attached_without_creating_anything()
    {
        var lead = await SeedLeadAsync();
        var existingId = Guid.NewGuid();
        _customers.ExistsAsync(_tenantId, existingId, Arg.Any<CancellationToken>()).Returns(true);

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, CustomerId: existingId, Force: true), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.CustomerId.Should().Be(existingId);

        await _customers.DidNotReceive().CreateFromLeadAsync(
            Arg.Any<CreateFromLeadRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_customer_id_is_refused_before_anything_is_created()
    {
        var lead = await SeedLeadAsync();
        _customers.ExistsAsync(_tenantId, Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Handler().Handle(
            new ConvertLeadCommand(lead.Id, CustomerId: Guid.NewGuid(), Force: true),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_NOT_FOUND");
    }
}
