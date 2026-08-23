namespace Sankore.Modules.Administration.Tests.Features.Users.AssignRole;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.AssignRole;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class AssignRoleHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public AssignRoleHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private (AssignRoleHandler handler, UserManager<AppUser> userManager, RoleManager<AppRole> roleManager)
        BuildHandler(ICurrentUser? currentUser = null)
    {
        var um = IdentityMockFactory.BuildUserManager();
        var rm = IdentityMockFactory.BuildRoleManager();
        var cu = currentUser ?? Substitute.For<ICurrentUser>();
        if (currentUser is null) cu.TenantId.Returns(_tenantId);
        var db = _factory.CreateContext();
        return (new AssignRoleHandler(db, um, rm, cu), um, rm);
    }

    // ── S1: Happy path — role assigned, UserRole row persisted ───────────

    [Fact]
    public async Task Assigns_role_and_persists_UserRole_record()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Fatou Diop", "fatou@test.sn");
        var role = AppRole.Create("Agent", "Agent");
        var roleId = Guid.NewGuid();
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(role);
        um.AddToRoleAsync(user, role.Name!).Returns(IdentityResult.Success);

        var result = await handler.Handle(new AssignRoleCommand(user.Id, roleId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.UserRoles.Should().ContainSingle(ur => ur.UserId == user.Id && ur.RoleId == roleId && ur.IsActive);
    }

    // ── S2: User not found (UserManager returns null) ─────────────────────

    [Fact]
    public async Task Fails_when_user_not_found()
    {
        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(Arg.Any<string>()).Returns((AppUser?)null);

        var result = await handler.Handle(new AssignRoleCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S3: User from a different tenant is rejected ──────────────────────

    [Fact]
    public async Task Fails_when_user_belongs_to_different_tenant()
    {
        var user = AppUser.Create(Guid.NewGuid(), Guid.NewGuid(), "Other Tenant", "other@test.sn");
        var (handler, um, _) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);

        var result = await handler.Handle(new AssignRoleCommand(user.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    // ── S4: Role not found or not assignable ──────────────────────────────

    [Fact]
    public async Task Fails_when_role_not_found()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Awa Ba", "awa@test.sn");
        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(Arg.Any<string>()).Returns((AppRole?)null);

        var result = await handler.Handle(new AssignRoleCommand(user.Id, Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task Fails_when_role_is_not_assignable()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Awa Ba", "awa@test.sn");
        var systemRole = AppRole.Create("System", "Compte technique", isSystem: true); // IsAssignable = false
        var roleId = Guid.NewGuid();
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(systemRole, roleId);

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(systemRole);

        var result = await handler.Handle(new AssignRoleCommand(user.Id, roleId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not assignable");
    }

    // ── S5: Duplicate active assignment rejected ──────────────────────────

    [Fact]
    public async Task Fails_when_user_already_has_role_active()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Ibou Fall", "ibou@test.sn");
        var role = AppRole.Create("Agent", "Agent");
        var roleId = Guid.NewGuid();
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);

        // Seed existing active UserRole
        await using var seed = _factory.CreateContext();
        seed.UserRoles.Add(UserRole.Assign(_tenantId, user.Id, roleId, Guid.NewGuid()));
        await seed.SaveChangesAsync();

        var (handler, um, rm) = BuildHandler();
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        rm.FindByIdAsync(roleId.ToString()).Returns(role);

        var result = await handler.Handle(new AssignRoleCommand(user.Id, roleId), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already has this role");
    }
}
