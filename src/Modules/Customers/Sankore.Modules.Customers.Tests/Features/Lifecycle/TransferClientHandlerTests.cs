namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Lifecycle;
using Sankore.Modules.Customers.Features.Lifecycle.TransferClient;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

public sealed class TransferClientHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SourceAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TargetAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid AdvisorId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private TransferClientHandler BuildHandler(
        IAgencyDirectory directory,
        out RecordingEventPublisher publisher,
        params Guid[] accessibleAgencies)
    {
        publisher = new RecordingEventPublisher();
        return new TransferClientHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            directory,
            publisher);
    }

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    private static IAgencyDirectory Directory(string? targetAgencyCode, bool advisorEligibleAtTarget)
    {
        var directory = Substitute.For<IAgencyDirectory>();
        directory.GetAgencyCodeAsync(Arg.Any<Guid>(), TargetAgencyId, Arg.Any<CancellationToken>())
            .Returns(targetAgencyCode);
        directory.IsAdvisorEligibleAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), TargetAgencyId, Arg.Any<CancellationToken>())
            .Returns(advisorEligibleAtTarget);
        return directory;
    }

    [Fact]
    public async Task Moves_the_client_to_the_target_agency_and_publishes_the_transfer_event()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(
            Directory("AG000002", advisorEligibleAtTarget: true), out var publisher);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, "Client moved to Bouaké"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgencyId.Should().Be(TargetAgencyId);
        result.Value.AgencyCode.Should().Be("AG000002");

        publisher.OfType<ClientTransferredEvent>().Should().ContainSingle()
            .Which.Should().Match<ClientTransferredEvent>(e =>
                e.TenantId == TenantId
                && e.ClientId == client.Id
                && e.FromAgencyId == SourceAgencyId
                && e.ToAgencyId == TargetAgencyId
                && e.ActorUserId == UserId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.AgencyId.Should().Be(TargetAgencyId);
        stored.AgencyCode.Should().Be("AG000002");
    }

    [Fact]
    public async Task Keeps_the_advisor_when_they_also_belong_to_the_target_agency()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId, advisorUserId: AdvisorId);
        await SeedAsync(client);

        var handler = BuildHandler(
            Directory("AG000002", advisorEligibleAtTarget: true), out var publisher);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, "Internal reorganisation"),
            CancellationToken.None);

        result.Value.AdvisorUserId.Should().Be(AdvisorId);
        publisher.OfType<ClientTransferredEvent>().Single().AdvisorUserId.Should().Be(AdvisorId);
    }

    [Fact]
    public async Task Resets_the_advisor_when_they_do_not_belong_to_the_target_agency()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId, advisorUserId: AdvisorId);
        await SeedAsync(client);

        var handler = BuildHandler(
            Directory("AG000002", advisorEligibleAtTarget: false), out var publisher);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, "Client moved to Bouaké"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AdvisorUserId.Should().BeNull();
        publisher.OfType<ClientTransferredEvent>().Single().AdvisorUserId.Should().BeNull();

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.AdvisorUserId.Should().BeNull();
    }

    [Fact]
    public async Task Reports_AGENCY_OUT_OF_SCOPE_when_the_target_agency_is_unknown_or_closed()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId);
        await SeedAsync(client);

        // A null agency code is how the directory reports "unknown or inactive".
        var handler = BuildHandler(Directory(null, advisorEligibleAtTarget: false), out var publisher);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, "Client moved"),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.AgencyOutOfScope);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_blank_motive_with_REASON_REQUIRED()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(Directory("AG000002", true), out _);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, " "), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ReasonRequired);
    }

    [Fact]
    public async Task Re_posting_the_current_agency_is_a_no_op_that_publishes_nothing()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(Directory("AG000002", true), out var publisher);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, SourceAgencyId, "Retry"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AgencyId.Should().Be(SourceAgencyId);
        publisher.Published.Should().BeEmpty();

        await using var assertions = _factory.CreateContext();
        var lines = await assertions.ClientStatusHistories.CountAsync(h => h.ClientId == client.Id);
        lines.Should().Be(2); // creation + KYC activation only
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_when_the_source_agency_is_outside_the_perimeter()
    {
        var client = ClientBuilder.Active(TenantId, SourceAgencyId, UserId);
        await SeedAsync(client);

        // The caller administers the destination but not the branch holding the client.
        var handler = BuildHandler(Directory("AG000002", true), out _, TargetAgencyId);

        var result = await handler.Handle(
            new TransferClientCommand(client.Id, TargetAgencyId, "Client moved"),
            CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    [Fact]
    public void Exposes_the_target_agency_through_IAgencyScopedRequest()
    {
        IAgencyScopedRequest command = new TransferClientCommand(Guid.NewGuid(), TargetAgencyId, "Move");

        command.TargetAgencyId.Should().Be(TargetAgencyId);
    }

    [Fact]
    public async Task Is_refused_before_the_handler_runs_when_the_target_agency_is_outside_the_perimeter()
    {
        // End-to-end check of the acceptance criterion: the pipeline behavior — not
        // the handler — is what stops a transfer towards a branch the operator does
        // not administer.
        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.CanAccessAgencyAsync(TenantId, UserId, TargetAgencyId, Arg.Any<CancellationToken>())
            .Returns(false);

        var behavior = new AgencyAuthorizationBehavior<TransferClientCommand, Result<ClientLifecycleStateDto>>(
            scope,
            TestDoubles.CurrentUser(TenantId, UserId),
            new FixedTenantContext(TenantId));

        var handlerRan = false;

        var response = await behavior.Handle(
            new TransferClientCommand(Guid.NewGuid(), TargetAgencyId, "Move"),
            () =>
            {
                handlerRan = true;
                return Task.FromResult(Result.Ok(
                    new ClientLifecycleStateDto(Guid.NewGuid(), "Active", "Approved", "Low",
                        TargetAgencyId, "AG000002", null, null, 1)));
            },
            CancellationToken.None);

        response.IsFailure.Should().BeTrue();
        response.Error.Should().Be(CustomerErrors.AgencyOutOfScope);
        handlerRan.Should().BeFalse();
    }
}
