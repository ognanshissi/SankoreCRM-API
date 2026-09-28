namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Modules.Kyc.PublicApi;
using Xunit;

public sealed class ReactivateClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ReactivateClientHandler BuildHandler(
        out RecordingEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingEventPublisher();
        return new ReactivateClientHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            publisher);
    }

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Reactivates_to_Active_when_the_kyc_file_is_approved_and_announces_the_activation()
    {
        // Suspended after a KYC approval: the file is still Approved.
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        client.KycStatus.Should().Be(KycStatus.Approved);
        await SeedAsync(client);

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new ReactivateClientCommand(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClientStatus.Active));

        publisher.OfType<ClientActivatedEvent>().Should().ContainSingle()
            .Which.ClientId.Should().Be(client.Id);
    }

    [Fact]
    public async Task Reactivates_back_to_PendingKyc_when_the_kyc_file_is_not_approved_and_announces_nothing()
    {
        // Build a suspended client whose KYC was rejected afterwards: the record is
        // not entitled to become Active again.
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        client.ApplyKycRejected("Blurred document", DateTimeOffset.UtcNow, UserId);
        client.KycStatus.Should().Be(KycStatus.Rejected);
        client.Status.Should().Be(ClientStatus.Suspended);
        await SeedAsync(client);

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new ReactivateClientCommand(client.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClientStatus.PendingKyc));

        // Not Active: nothing downstream may believe the client can transact.
        publisher.OfType<ClientActivatedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task Records_a_status_history_line_for_the_reactivation()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _);

        await handler.Handle(new ReactivateClientCommand(client.Id), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var line = await assertions.ClientStatusHistories
            .Where(h => h.ClientId == client.Id && h.OldStatus == ClientStatus.Suspended)
            .SingleAsync();

        line.NewStatus.Should().Be(ClientStatus.Active);
        line.ActorUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task Refuses_to_reactivate_a_client_that_is_not_suspended_with_INVALID_STATUS_TRANSITION()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new ReactivateClientCommand(client.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.InvalidStatusTransition);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_to_reactivate_an_archived_client_with_CLIENT_READ_ONLY()
    {
        var client = ClientBuilder.Archived(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new ReactivateClientCommand(client.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_outside_the_agency_perimeter()
    {
        var client = ClientBuilder.Suspended(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _, OtherAgencyId);

        var result = await handler.Handle(
            new ReactivateClientCommand(client.Id), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
