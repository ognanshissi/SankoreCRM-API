namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle;
using Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class ArchiveClientHandlerTests : IDisposable
{
    private const string ClientHasActiveCommitments = "CLIENT_HAS_ACTIVE_COMMITMENTS";

    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private ArchiveClientHandler BuildHandler(
        out RecordingEventPublisher publisher,
        bool hasActiveCommitments = false,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingEventPublisher();
        return new ArchiveClientHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            LifecycleTestDoubles.BalanceProbe(hasActiveCommitments),
            publisher);
    }

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Archives_the_client_stamps_the_date_and_publishes_the_archive_event()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(
            new ArchiveClientCommand(client.Id, "Account closed at the client's request"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(ClientStatus.Archived));
        result.Value.ArchivedAt.Should().NotBeNull();

        publisher.OfType<ClientArchivedEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientArchivedEvent>(e =>
                e.TenantId == TenantId
                && e.ClientId == client.Id
                && e.Reason == "Account closed at the client's request"
                && e.ActorUserId == UserId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.Status.Should().Be(ClientStatus.Archived);
        stored.ArchivedAt.Should().NotBeNull();
        stored.IsReadOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Records_a_status_history_line_for_the_archive()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _);

        await handler.Handle(
            new ArchiveClientCommand(client.Id, "Account closed"), CancellationToken.None);

        await using var assertions = _factory.CreateContext();
        var line = await assertions.ClientStatusHistories
            .Where(h => h.ClientId == client.Id && h.NewStatus == ClientStatus.Archived)
            .SingleAsync();

        line.OldStatus.Should().Be(ClientStatus.Active);
        line.Reason.Should().Be("Account closed");
        line.ActorUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task Refuses_to_archive_a_client_with_active_commitments()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out var publisher, hasActiveCommitments: true);

        var result = await handler.Handle(
            new ArchiveClientCommand(client.Id, "Account closed"), CancellationToken.None);

        result.Error.Should().Be(ClientHasActiveCommitments);
        publisher.Published.Should().BeEmpty();

        // The probe is consulted BEFORE the transition, so nothing at all changed.
        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.Status.Should().Be(ClientStatus.Active);
        stored.ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new ArchiveClientCommand(client.Id, "  "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Refuses_to_archive_an_already_archived_client_with_CLIENT_READ_ONLY()
    {
        var client = ClientBuilder.Archived(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _);

        var result = await handler.Handle(
            new ArchiveClientCommand(client.Id, "Account closed"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_outside_the_agency_perimeter()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(out _, false, OtherAgencyId);

        var result = await handler.Handle(
            new ArchiveClientCommand(client.Id, "Account closed"), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public async Task The_default_probe_reports_no_commitment_so_the_rule_stays_dormant_until_M03_M04()
    {
        var probe = new NoOutstandingBalanceProbe();

        var hasCommitments = await probe.HasActiveCommitmentsAsync(
            TenantId, Guid.NewGuid(), CancellationToken.None);

        hasCommitments.Should().BeFalse();
    }
}
