namespace Sankore.Modules.Customers.Tests.Features.Groups;

using FluentAssertions;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Groups.AddGroupMember;
using Sankore.Modules.Customers.Features.Groups.AssignOfficeRole;
using Sankore.Modules.Customers.Features.Groups.CreateGroup;
using Sankore.Modules.Customers.Features.Groups.DissolveGroup;
using Sankore.Modules.Customers.Features.Groups.RemoveGroupMember;
using Sankore.Modules.Customers.Features.Groups.SuspendGroup;
using Xunit;

public sealed class GroupsValidatorTests
{
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid GroupId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ClientId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly DateOnly Constituted = new(2026, 1, 15);

    [Fact]
    public void CreateGroup_accepts_a_well_formed_command()
    {
        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand(GroupType.Tontine, "Tontine Wari", AgencyId, Constituted));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    public void CreateGroup_refuses_a_blank_or_too_short_name(string name)
    {
        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand(GroupType.Tontine, name, AgencyId, Constituted));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateGroupCommand.Name));
    }

    [Fact]
    public void CreateGroup_refuses_a_name_longer_than_the_column()
    {
        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand(GroupType.Tontine, new string('x', 151), AgencyId, Constituted));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateGroup_refuses_an_empty_agency()
    {
        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand(GroupType.Tontine, "Tontine Wari", Guid.Empty, Constituted));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateGroupCommand.AgencyId));
    }

    [Fact]
    public void CreateGroup_refuses_a_constitution_date_in_the_future()
    {
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);

        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand(GroupType.Tontine, "Tontine Wari", AgencyId, tomorrow));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateGroupCommand.ConstitutionDate));
    }

    [Fact]
    public void CreateGroup_refuses_an_unknown_group_type()
    {
        var result = new CreateGroupValidator().Validate(
            new CreateGroupCommand((GroupType)99, "Tontine Wari", AgencyId, Constituted));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void AddGroupMember_refuses_an_unknown_office_role()
    {
        var result = new AddGroupMemberValidator().Validate(
            new AddGroupMemberCommand(GroupId, ClientId, (GroupOfficeRole)42));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void AddGroupMember_accepts_a_well_formed_command()
    {
        var result = new AddGroupMemberValidator().Validate(
            new AddGroupMemberCommand(GroupId, ClientId, GroupOfficeRole.President));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void AssignOfficeRole_refuses_an_empty_client()
    {
        var result = new AssignOfficeRoleValidator().Validate(
            new AssignOfficeRoleCommand(GroupId, Guid.Empty, GroupOfficeRole.President));

        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("no")]
    public void RemoveGroupMember_refuses_a_blank_or_too_short_motive(string reason)
    {
        var result = new RemoveGroupMemberValidator().Validate(
            new RemoveGroupMemberCommand(GroupId, ClientId, reason));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "REASON_REQUIRED");
    }

    [Fact]
    public void SuspendGroup_and_DissolveGroup_both_require_a_motive()
    {
        new SuspendGroupValidator().Validate(new SuspendGroupCommand(GroupId, "  "))
            .Errors.Should().Contain(e => e.ErrorMessage == "REASON_REQUIRED");

        new DissolveGroupValidator().Validate(new DissolveGroupCommand(GroupId, string.Empty))
            .Errors.Should().Contain(e => e.ErrorMessage == "REASON_REQUIRED");
    }

    [Fact]
    public void SuspendGroup_accepts_a_motive_within_bounds()
    {
        new SuspendGroupValidator().Validate(
                new SuspendGroupCommand(GroupId, "Repayment incident under review"))
            .IsValid.Should().BeTrue();
    }
}
