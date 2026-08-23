namespace Sankore.Modules.Administration.Tests.Features.Users.RevokeRole;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.RevokeRole;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class RevokeRoleHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public RevokeRoleHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private (RevokeRoleHandler handler, UserManager<AppUser> um, RoleManager<AppRole> rm)
        BuildHandler()
    {
        var um = IdentityMockFactory.BuildUserManager();
        var rm = IdentityMockFactory.BuildRoleManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        var db = _factory.CreateContext();
        return (new RevokeRoleHandler(db, um, rm, cu), um, rm);
    }

    // ── S1: Happy path — UserRole marked inactive ─────────────────────────

    [Fact]
    public async Task Revokes_role_and_marks_UserRole_inactive()
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();

        await using var seed = _factory.CreateContext();
        seed.UserRoles.Add(UserRole.Assign(_tenantId, userId, roleId, Guid.NewGuid()));
        await seed.SaveChangesAsync();

        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Oumar Ndiaye", "oumar@test.sn");
        typeof(AppUser).GetProperty(nameof(AppUser.Id))!.SetValue(user, userId);

        var role = AppRole.Create("Agent", "Agent");
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(userId.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(role);
        um.RemoveFromRoleAsync(user, role.Name!).Returns(IdentityResult.Success);

        var result = await handler.Handle(new RevokeRoleCommand(userId, roleId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.UserRoles.Where(ur => ur.UserId == userId && ur.RoleId == roleId)
            .Should().AllSatisfy(ur => ur.IsActive.Should().BeFalse());
    }

    // ── S2: User not found ────────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_user_not_found()
    {
        var (handler, um, _) = BuildHandler();
        um.FindByIdAsync(Arg.Any<string>()).Returns((AppUser?)null);

        var result = await handler.Handle(new RevokeRoleCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S3: Role not found ────────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_role_not_found()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Test", "test@test.sn");
        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(Arg.Any<string>()).Returns((AppRole?)null);

        var result = await handler.Handle(new RevokeRoleCommand(user.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S4: System role cannot be revoked ─────────────────────────────────

    [Fact]
    public async Task Fails_when_revoking_System_role()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Root", "root@test.sn");
        var systemRole = AppRole.Create("System", "Compte technique", isSystem: true);
        var roleId = Guid.NewGuid();
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(systemRole, roleId);

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(systemRole);

        var result = await handler.Handle(new RevokeRoleCommand(user.Id, roleId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("System role cannot be revoked");
    }

    // ── S5: User does not have the role ───────────────────────────────────

    [Fact]
    public async Task Fails_when_user_does_not_have_the_role()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Ndeye Seck", "ndeye@test.sn");
        var role = AppRole.Create("Agent", "Agent");
        var roleId = Guid.NewGuid();
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(role);

        var result = await handler.Handle(new RevokeRoleCommand(user.Id, roleId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("does not have this role");
    }
}
