namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Sankore.Modules.Administration.PublicApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.ClickToCall;
using Sankore.Modules.Integration.Features.Telephony.UserLinks.LinkUser;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Placing a call from a record (TEL-B-06).
///
/// <para>
/// The property every case protects: the agent names a RECORD and a SLOT, the number is read on
/// the server by the module that owns it, and nothing in the request or the response is a
/// telephone number.
/// </para>
/// </summary>
public sealed class RequestClickToCallTests : IDisposable
{
    private const string ClientNumber = "+22507222222";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _agent = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    private ICustomersModule _customers = Substitute.For<ICustomersModule>();
    private ILeadsModule _leads = Substitute.For<ILeadsModule>();

    public RequestClickToCallTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        NumberIs(Result.Ok(ClientNumber));
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_linked_agent_gets_a_request_row_and_the_provider_is_called()
    {
        await LinkAsync();

        var result = await DialAsync();

        result.IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var request = db.TelClickToCalls.Single();

        request.Id.Should().Be(result.Value);
        request.CrmUserId.Should().Be(_agent);
        request.EntityType.Should().Be(TelEntityTypes.Customer);
        request.EntityId.Should().Be(_clientId);
        request.PhoneSlot.Should().Be(nameof(TelephonyPhoneSlot.Primary));

        // The provider answered with a reference, so the request is correlatable by identity
        // rather than by the two-minute fallback.
        request.Status.Should().Be(TelClickToCallStatus.AgentRinging);
        request.ExternalRef.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_test_tenant_does_not_ring_a_customer_and_records_no_attempt()
    {
        await LinkAsync();
        var administration = Substitute.For<IAdministrationModule>();
        administration.IsPhoneBlockedInTestAsync(_tenant, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var result = await DialAsync(administration: administration);

        result.Error.Should().Be(TelephonyErrors.TestNumber);
        await using var db = _factory.CreateContext();
        db.TelClickToCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_dialled_number_is_stored_encrypted_and_indexed()
    {
        await LinkAsync();

        await DialAsync();

        await using var db = _factory.CreateContext();
        var request = db.TelClickToCalls.Single();

        // Kept for the correlation window — and kept the way every number in this family is.
        Encoding.UTF8.GetString(request.TargetNumberEnc)
            .Should().StartWith("v1:").And.NotContain("07222222");

        request.TargetNumberBi.Should().Equal(TelephonyTestHarness.Numbers().IndexOf(ClientNumber));
    }

    [Fact]
    public async Task An_agent_with_no_provider_account_is_refused_before_anything_is_written()
    {
        var result = await DialAsync();

        result.Error.Should().Be(TelephonyErrors.UserNotLinked);

        await using var db = _factory.CreateContext();
        db.TelClickToCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_agent_whose_link_is_broken_cannot_dial()
    {
        await LinkAsync();

        await using (var db = _factory.CreateContext())
        {
            var link = await db.TelUserLinks.AsTracking().SingleAsync(CancellationToken.None);
            link.MarkBroken("The account is no longer in the provider's directory.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // Unlike the journal's attribution, which still credits a broken link: "whose
        // conversation was this" is answerable for an agent whose account vanished, "which
        // handset do I ring" is not.
        (await DialAsync()).Error.Should().Be(TelephonyErrors.UserNotLinked);
    }

    [Fact]
    public async Task The_owning_modules_refusal_is_relayed_unchanged()
    {
        await LinkAsync();

        // An agent of one branch dialling another branch's client. M01 folds that into
        // CLIENT_NOT_FOUND, and relaying it unchanged is what tells the agent this act is not
        // theirs — rather than a generic telephony error they would report as a bug.
        NumberIs(Result.Fail<string>("CLIENT_NOT_FOUND"));

        var result = await DialAsync();

        result.Error.Should().Be("CLIENT_NOT_FOUND");

        await using var db = _factory.CreateContext();
        db.TelClickToCalls.Should().BeEmpty("nothing is recorded for a call that was never placed");
    }

    [Fact]
    public async Task A_slot_that_holds_no_number_is_refused_and_says_so()
    {
        await LinkAsync();

        NumberIs(Result.Fail<string>("UNKNOWN_SENSITIVE_FIELD"));

        (await DialAsync()).Error.Should().Be("UNKNOWN_SENSITIVE_FIELD");
    }

    [Fact]
    public async Task A_provider_refusal_keeps_the_attempt_on_record()
    {
        await LinkAsync();

        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Functional(IntegrationErrors.PayloadInvalid),
        };

        var result = await DialAsync(adapter);

        result.Error.Should().Be(TelephonyErrors.ProviderRefused);

        await using var db = _factory.CreateContext();
        var request = db.TelClickToCalls.Single();

        // THE case the suppressed transaction exists for: this handler returns Result.Fail, which
        // rolls the ambient scope back — so without it the Failed row, the only record that an
        // agent pressed the button, would be gone.
        request.Status.Should().Be(TelClickToCallStatus.Failed);
        request.ErrorCode.Should().Be(IntegrationErrors.PayloadInvalid);
    }

    [Fact]
    public async Task A_tenant_with_no_telephony_cannot_dial()
    {
        await LinkAsync();

        await using (var db = _factory.CreateContext())
        {
            var connection = await db.Connections.AsTracking().SingleAsync(CancellationToken.None);
            connection.Deactivate(TelephonyTestHarness.Actor, TelephonyTestHarness.Clock);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await DialAsync()).Error.Should().Be(TelephonyErrors.Disabled);
    }

    [Fact]
    public async Task An_unknown_entity_type_is_refused_by_the_resolver()
    {
        await LinkAsync();

        var result = await DialAsync(entityType: "Supplier");

        // The validator refuses this shape at the HTTP edge; the resolver refuses it again
        // because it is also reachable from the gateway, and a closed list checked in one place
        // is a closed list somebody routes around.
        result.Error.Should().Be(TelephonyErrors.EntityTypeUnknown);
    }

    [Fact]
    public async Task A_lead_is_dialled_through_M13_and_a_client_through_M01()
    {
        await LinkAsync();

        await DialAsync(entityType: TelEntityTypes.Lead);

        await _leads.Received(1).ResolvePhoneForCallAsync(
            _tenant, _clientId, Arg.Any<Sankore.Modules.Leads.PublicApi.PhoneSlot>(), _agent,
            Arg.Any<CancellationToken>());

        await _customers.DidNotReceiveWithAnyArgs().ResolvePhoneForCallAsync(
            default, default, default, default, default);
    }

    [Fact]
    public async Task The_actor_passed_to_the_owning_module_is_the_caller_and_not_the_record()
    {
        await LinkAsync();

        await DialAsync();

        // The owning module evaluates its perimeter against this id and refuses one that is not
        // the ambient identity. Passing anything else would be asking it to vouch for a check it
        // never made.
        await _customers.Received(1).ResolvePhoneForCallAsync(
            _tenant, _clientId, Arg.Any<Sankore.Modules.Customers.PublicApi.PhoneSlot>(), _agent,
            Arg.Any<CancellationToken>());
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private void NumberIs(Result<string> answer)
    {
        _customers = Substitute.For<ICustomersModule>();
        _leads = Substitute.For<ILeadsModule>();

        _customers.ResolvePhoneForCallAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<Sankore.Modules.Customers.PublicApi.PhoneSlot>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(answer);

        _leads.ResolvePhoneForCallAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<Sankore.Modules.Leads.PublicApi.PhoneSlot>(), Arg.Any<Guid>(),
                Arg.Any<CancellationToken>())
            .Returns(answer);
    }

    private async Task LinkAsync()
    {
        await using var db = _factory.CreateContext();

        var result = await new LinkTelephonyUserHandler(
                db,
                TelephonyTestHarness.Access(db, new FakeAdapter()),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<LinkTelephonyUserHandler>.Instance)
            .Handle(
                new LinkTelephonyUserCommand(_agent, FakeAdapter.SeededUser.Id.Value),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    private async Task<Result<Guid>> DialAsync(
        FakeAdapter? adapter = null,
        string entityType = TelEntityTypes.Customer,
        TelephonyPhoneSlot slot = TelephonyPhoneSlot.Primary,
        IAdministrationModule? administration = null)
    {
        await using var db = _factory.CreateContext();

        return await new RequestClickToCallHandler(
                db,
                TelephonyTestHarness.Access(db, adapter ?? new FakeAdapter()),
                new DialableNumberResolver(
                    _customers, _leads, NullLogger<DialableNumberResolver>.Instance),
                TelephonyTestHarness.Numbers(),
                TelephonyTestHarness.User(_tenant, _agent),
                TelephonyTestHarness.Clock,
                NullLogger<RequestClickToCallHandler>.Instance,
                administration)
            .Handle(new RequestClickToCallCommand(entityType, _clientId, slot), CancellationToken.None);
    }
}
