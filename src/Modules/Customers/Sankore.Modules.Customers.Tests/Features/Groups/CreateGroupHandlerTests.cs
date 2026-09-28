namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.CreateGroup;
using Sankore.Modules.Customers.Features.Groups.Shared;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Customers.Tests.TestSupport;
using Xunit;

public sealed class CreateGroupHandlerTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");

    private readonly TestCustomersDbContextFactory _factory = new(TenantId);

    public void Dispose() => _factory.Dispose();

    private CreateGroupHandler BuildHandler(out RecordingGroupEventPublisher publisher)
    {
        publisher = new RecordingGroupEventPublisher();
        return new CreateGroupHandler(
            _factory.CreateContext(),
            TestDoubles.CurrentUser(TenantId, UserId),
            publisher);
    }

    private static CreateGroupCommand Command(string name = "Groupe Nimba", Guid? agencyId = null)
        => new(GroupType.SolidarityGroup, name, agencyId ?? AgencyId, new DateOnly(2026, 1, 15));

    [Fact]
    public async Task Creates_the_group_in_status_Forming()
    {
        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(nameof(GroupStatus.Forming));
        result.Value.Name.Should().Be("Groupe Nimba");
        result.Value.Type.Should().Be(nameof(GroupType.SolidarityGroup));

        await using var assertions = _factory.CreateContext();
        var stored = await assertions.ClientGroups.SingleAsync(g => g.Id == result.Value.GroupId);
        stored.Status.Should().Be(GroupStatus.Forming);
        stored.AgencyId.Should().Be(AgencyId);
        stored.CreatedBy.Should().Be(UserId);
        stored.ActiveMemberCount.Should().Be(0);
    }

    [Fact]
    public async Task Publishes_GroupCreatedEvent_through_the_outbox_before_saving()
    {
        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(Command(), CancellationToken.None);

        publisher.OfType<GroupCreatedEvent>().Should().ContainSingle()
            .Which.Should().Match<GroupCreatedEvent>(e =>
                e.TenantId == TenantId
                && e.GroupId == result.Value.GroupId
                && e.Name == "Groupe Nimba"
                && e.GroupType == nameof(GroupType.SolidarityGroup)
                && e.AgencyId == AgencyId);
    }

    [Fact]
    public async Task Trims_the_name_before_storing_it()
    {
        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command("  Groupe Nimba  "), CancellationToken.None);

        result.Value.Name.Should().Be("Groupe Nimba");
    }

    [Fact]
    public async Task Refuses_a_name_already_used_in_the_same_agency()
    {
        await using (var seed = _factory.CreateContext())
        {
            await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, AgencyId, name: "Groupe Nimba"));
        }

        var handler = BuildHandler(out var publisher);

        var result = await handler.Handle(Command("Groupe Nimba"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupNameAlreadyUsed);
        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepts_the_same_name_in_another_agency()
    {
        await using (var seed = _factory.CreateContext())
        {
            await TestClientFactory.SeedGroupAsync(
                seed, TestClientFactory.Group(TenantId, OtherAgencyId, name: "Groupe Nimba"));
        }

        var handler = BuildHandler(out _);

        var result = await handler.Handle(Command("Groupe Nimba"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Turns_the_unique_index_violation_of_a_concurrent_creation_into_GROUP_NAME_ALREADY_USED()
    {
        // Two operators submit the same name at the same instant: both pass the
        // AnyAsync pre-check, and the loser is rejected by ux_client_groups_name.
        var handler = new CreateGroupHandler(
            GroupTestData.FailingContext(
                $"groups-race-{Guid.NewGuid()}", TenantId, GroupUniqueViolation.GroupNameIndex),
            TestDoubles.CurrentUser(TenantId, UserId),
            new RecordingGroupEventPublisher());

        var result = await handler.Handle(Command(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.GroupNameAlreadyUsed);
    }

    [Fact]
    public async Task Creates_a_tontine_and_a_vsla_just_as_well()
    {
        var handler = BuildHandler(out _);

        var tontine = await handler.Handle(
            new CreateGroupCommand(GroupType.Tontine, "Tontine Wari", AgencyId, new DateOnly(2026, 2, 1)),
            CancellationToken.None);

        var vsla = await handler.Handle(
            new CreateGroupCommand(GroupType.Vsla, "AVEC Sikasso", AgencyId, new DateOnly(2026, 2, 2)),
            CancellationToken.None);

        tontine.Value.Type.Should().Be(nameof(GroupType.Tontine));
        vsla.Value.Type.Should().Be(nameof(GroupType.Vsla));
    }
}
