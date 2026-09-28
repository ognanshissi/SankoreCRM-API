namespace Sankore.Modules.Customers.Tests.Domain;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Domain.Events;
using Xunit;

public class ClientGroupTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid AgencyId = Guid.NewGuid();
    private static readonly Guid ActorId = Guid.NewGuid();

    [Fact]
    public void Create_should_start_a_forming_group_and_raise_a_creation_event()
    {
        var group = NewGroup();

        group.Status.Should().Be(GroupStatus.Forming);
        group.ActiveMemberCount.Should().Be(0);
        group.DomainEvents.OfType<GroupCreatedDomainEvent>().Should().ContainSingle()
            .Which.GroupId.Should().Be(group.Id);
    }

    [Fact]
    public void TryActivate_should_leave_the_group_forming_below_the_minimum_size()
    {
        var group = NewGroup();
        var now = DateTimeOffset.UtcNow;
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.President, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Treasurer, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Secretary, now, ActorId);

        var result = group.TryActivate(minSize: 5);

        result.IsSuccess.Should().BeTrue();
        group.Status.Should().Be(GroupStatus.Forming);
    }

    [Fact]
    public void TryActivate_should_leave_the_group_forming_when_an_office_is_vacant()
    {
        var group = NewGroup();
        var now = DateTimeOffset.UtcNow;
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.President, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Treasurer, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Member, now, ActorId);

        var result = group.TryActivate(minSize: 3);

        result.IsSuccess.Should().BeTrue();
        group.Status.Should().Be(GroupStatus.Forming, "no secretary has been appointed yet");
    }

    [Fact]
    public void TryActivate_should_activate_the_group_once_size_and_offices_are_satisfied()
    {
        var group = ConstitutedGroup();

        var result = group.TryActivate(minSize: 3);

        result.IsSuccess.Should().BeTrue();
        group.Status.Should().Be(GroupStatus.Active);
    }

    [Fact]
    public void TryActivate_on_a_group_that_is_no_longer_forming_should_fail()
    {
        var group = ConstitutedGroup();
        group.TryActivate(3);

        group.TryActivate(3).Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }

    [Fact]
    public void AssignOfficeRole_should_demote_the_previous_holder_to_member()
    {
        var group = NewGroup();
        var now = DateTimeOffset.UtcNow;
        var firstPresident = Guid.NewGuid();
        var newPresident = Guid.NewGuid();
        group.AddMember(firstPresident, GroupOfficeRole.President, now, ActorId);
        group.AddMember(newPresident, GroupOfficeRole.Member, now, ActorId);

        var result = group.AssignOfficeRole(newPresident, GroupOfficeRole.President, ActorId);

        result.IsSuccess.Should().BeTrue();
        group.Memberships.Single(m => m.ClientId == newPresident).OfficeRole.Should().Be(GroupOfficeRole.President);
        group.Memberships.Single(m => m.ClientId == firstPresident).OfficeRole.Should().Be(GroupOfficeRole.Member);
    }

    [Fact]
    public void AssignOfficeRole_should_report_an_unknown_member()
    {
        var group = ConstitutedGroup();

        group.AssignOfficeRole(Guid.NewGuid(), GroupOfficeRole.Treasurer, ActorId)
            .Error.Should().Be(CustomerErrors.MembershipNotFound);
    }

    [Fact]
    public void RemoveMember_should_close_the_membership_without_deleting_it()
    {
        var group = ConstitutedGroup();
        var leaving = group.Memberships.First().ClientId;
        var at = DateTimeOffset.UtcNow;

        var result = group.RemoveMember(leaving, "Départ du village", at, ActorId);

        result.IsSuccess.Should().BeTrue();
        group.Memberships.Should().HaveCount(3, "history is preserved");
        var membership = group.Memberships.Single(m => m.ClientId == leaving);
        membership.IsActive.Should().BeFalse();
        membership.LeftAt.Should().Be(at);
        membership.LeaveReason.Should().Be("Départ du village");
        group.ActiveMemberCount.Should().Be(2);
        group.DomainEvents.OfType<GroupMembershipChangedDomainEvent>()
            .Should().Contain(e => e.ClientId == leaving && e.Change == "Removed");
    }

    [Fact]
    public void RemoveMember_should_report_an_unknown_member()
    {
        var group = ConstitutedGroup();

        group.RemoveMember(Guid.NewGuid(), "Inconnu", DateTimeOffset.UtcNow, ActorId)
            .Error.Should().Be(CustomerErrors.MembershipNotFound);
    }

    [Fact]
    public void AddMember_should_refuse_the_same_client_twice()
    {
        var group = NewGroup();
        var clientId = Guid.NewGuid();
        group.AddMember(clientId, GroupOfficeRole.Member, DateTimeOffset.UtcNow, ActorId);

        var act = () => group.AddMember(clientId, GroupOfficeRole.Member, DateTimeOffset.UtcNow, ActorId);

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>()
            .Which.MessageKey.Should().Be("GroupMembership.AlreadyMember");
    }

    [Fact]
    public void Dissolve_should_close_every_active_membership_and_mark_the_group_dissolved()
    {
        var group = ConstitutedGroup();
        group.TryActivate(3);
        var at = DateTimeOffset.UtcNow;

        var result = group.Dissolve("Fin du cycle", at, ActorId);

        result.IsSuccess.Should().BeTrue();
        group.Status.Should().Be(GroupStatus.Dissolved);
        group.DissolvedAt.Should().Be(at);
        group.DissolutionReason.Should().Be("Fin du cycle");
        group.ActiveMemberCount.Should().Be(0);
        group.Memberships.Should().HaveCount(3).And.OnlyContain(m => m.LeftAt == at);
        group.DomainEvents.OfType<GroupDissolvedDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void Dissolve_without_a_reason_should_fail_and_a_dissolved_group_accepts_nothing_more()
    {
        var group = ConstitutedGroup();

        group.Dissolve("  ", DateTimeOffset.UtcNow, ActorId).Error.Should().Be(CustomerErrors.ReasonRequired);
        group.Dissolve("Fin", DateTimeOffset.UtcNow, ActorId).IsSuccess.Should().BeTrue();
        group.Dissolve("Encore", DateTimeOffset.UtcNow, ActorId).Error.Should().Be(CustomerErrors.InvalidStatusTransition);
        group.RemoveMember(Guid.NewGuid(), "x", DateTimeOffset.UtcNow, ActorId)
            .Error.Should().Be(CustomerErrors.InvalidStatusTransition);
    }

    [Fact]
    public void Suspend_should_require_a_reason()
    {
        var group = ConstitutedGroup();

        group.Suspend("", ActorId).Error.Should().Be(CustomerErrors.ReasonRequired);
        group.Suspend("Impayés", ActorId).IsSuccess.Should().BeTrue();
        group.Status.Should().Be(GroupStatus.Suspended);
    }

    // ── Builders ────────────────────────────────────────────────────────────

    private static ClientGroup NewGroup() =>
        ClientGroup.Create(TenantId, GroupType.SolidarityGroup, "Groupe Espoir", AgencyId,
            new DateOnly(2026, 1, 15), ActorId);

    /// <summary>Three members, all three offices filled — ready to be activated with minSize 3.</summary>
    private static ClientGroup ConstitutedGroup()
    {
        var group = NewGroup();
        var now = DateTimeOffset.UtcNow;
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.President, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Treasurer, now, ActorId);
        group.AddMember(Guid.NewGuid(), GroupOfficeRole.Secretary, now, ActorId);
        return group;
    }
}
