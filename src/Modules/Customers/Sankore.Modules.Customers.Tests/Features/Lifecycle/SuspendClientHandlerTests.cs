namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class SuspendClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private SuspendClientHandler BuildHandler(
        out RecordingEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingEventPublisher();
        return new SuspendClientHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            publisher);
    }

    [Fact]
    public async Task Suspends_an_active_client_and_publishes_the_suspension_event()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new SuspendClientCommand(client.Id, "Fraud suspicion"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClientStatus.Suspended));

        publisher.OfType<ClientSuspendedEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientSuspendedEvent>(e =>
                e.TenantId == TenantId && e.ClientId == client.Id
                && e.Reason == "Fraud suspicion" && e.ActorUserId == UserId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.Status.Should().Be(ClientStatus.Suspended);
    }

    [Fact]
    public async Task Records_a_status_history_line_carrying_the_motive_and_the_actor()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        await handler.Handle(new SuspendClientCommand(client.Id, "Fraud suspicion"), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var line = await assertions.ClientStatusHistories
            .Where(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.Suspended)
            .SingleAsync();

        line.OldStatus.Should().Be(ClientStatus.Active);
        line.Reason.Should().Be("Fraud suspicion");
        line.ActorUserId.Should().Be(UserId);
        line.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new SuspendClientCommand(client.Id, "   "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_to_suspend_a_client_that_is_not_active_with_INVALID_STATUS_TRANSITION()
    {
        // PendingKyc is not a suspendable state: there is nothing to suspend yet.
        var client = ClientBuilder.PendingKyc(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new SuspendClientCommand(client.Id, "Fraud suspicion"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_to_suspend_an_archived_client_with_CLIENT_READ_ONLY()
    {
        var client = ClientBuilder.Archived(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new SuspendClientCommand(client.Id, "Fraud suspicion"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_and_never_AGENCY_OUT_OF_SCOPE_outside_the_agency_perimeter()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(client);
            await seed.SaveChangesAsync();
        }

        // The caller only sees another agency: the record must look non-existent.
        var handler = BuildHandler(out _, OtherAgencyId);

        var result = await handler.Handle(
            new SuspendClientCommand(client.Id, "Fraud suspicion"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
        result.Error.Should().NotBe(CustomerErrors.AgencyOutOfScope);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_for_an_unknown_client()
    {
        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new SuspendClientCommand(Guid.NewGuid(), "Fraud suspicion"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task Cannot_reach_a_client_of_another_tenant()
    {
        // Same shared in-memory store, different tenant on the row: the query filter
        // of the ambient tenant must hide it completely.
        var foreign = ClientBuilder.Active(OtherTenantId, AgencyId, UserId);
        await using (var seed = _factory.CreateContext())
        {
            seed.Clients.Add(foreign);
            await seed.SaveChangesAsync();
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new SuspendClientCommand(foreign.Id, "Fraud suspicion"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
