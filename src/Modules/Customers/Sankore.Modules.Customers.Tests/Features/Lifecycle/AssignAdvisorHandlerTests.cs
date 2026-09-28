namespace Sankore.Modules.Customers.Tests.Features.Lifecycle;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Clients.Shared;
using Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class AssignAdvisorHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid AdvisorId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private AssignAdvisorHandler BuildHandler(IAgencyDirectory directory, params Guid[] accessibleAgencies)
        => new(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            TestDoubles.AgencyScope(accessibleAgencies),
            directory);

    private async Task SeedAsync(Client client)
    {
        await using var seed = _factory.CreateContext();
        seed.Clients.Add(client);
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task Assigns_an_eligible_advisor_of_the_clients_own_agency()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(LifecycleTestDoubles.PermissiveAgencyDirectory());

        var result = await handler.Handle(
            new AssignAdvisorCommand(client.Id, AdvisorId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AdvisorUserId.Should().Be(AdvisorId);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.AdvisorUserId.Should().Be(AdvisorId);
    }

    [Fact]
    public async Task Checks_eligibility_against_the_clients_agency_not_the_callers()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var directory = LifecycleTestDoubles.PermissiveAgencyDirectory();
        var handler = BuildHandler(directory);

        await handler.Handle(new AssignAdvisorCommand(client.Id, AdvisorId), CancellationToken.None);

        await directory.Received(1).IsAdvisorEligibleAsync(
            TenantId, AdvisorId, AgencyId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Refuses_an_advisor_who_is_inactive_or_from_another_agency_with_ADVISOR_NOT_ELIGIBLE()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var directory = Substitute.For<IAgencyDirectory>();
        directory.IsAdvisorEligibleAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var handler = BuildHandler(directory);

        var result = await handler.Handle(
            new AssignAdvisorCommand(client.Id, AdvisorId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.AdvisorNotEligible);

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.Clients.SingleAsync(c => c.Id == client.Id);
        stored.AdvisorUserId.Should().BeNull();
    }

    [Fact]
    public async Task Clears_the_advisor_without_any_eligibility_check_when_the_id_is_null()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId, advisorUserId: AdvisorId);
        await SeedAsync(client);

        var directory = Substitute.For<IAgencyDirectory>();
        var handler = BuildHandler(directory);

        var result = await handler.Handle(
            new AssignAdvisorCommand(client.Id, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AdvisorUserId.Should().BeNull();

        await directory.DidNotReceiveWithAnyArgs().IsAdvisorEligibleAsync(
            default, default, default, default);
    }

    [Fact]
    public async Task Refuses_to_touch_an_archived_client_with_CLIENT_READ_ONLY()
    {
        var client = ClientBuilder.Archived(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(LifecycleTestDoubles.PermissiveAgencyDirectory());

        var result = await handler.Handle(
            new AssignAdvisorCommand(client.Id, AdvisorId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientReadOnly);
    }

    [Fact]
    public async Task Reports_CLIENT_NOT_FOUND_outside_the_agency_perimeter()
    {
        var client = ClientBuilder.Active(TenantId, AgencyId, UserId);
        await SeedAsync(client);

        var handler = BuildHandler(LifecycleTestDoubles.PermissiveAgencyDirectory(), OtherAgencyId);

        var result = await handler.Handle(
            new AssignAdvisorCommand(client.Id, AdvisorId), CancellationToken.None);

        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }
}
