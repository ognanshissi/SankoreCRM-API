namespace Sankore.Modules.Administration.Tests.Features.Users.GetUserPermissions;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.GetUserPermissions;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class GetUserPermissionsHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public GetUserPermissionsHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private GetUserPermissionsHandler BuildHandler(AppUser? returnUser = null)
    {
        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);

        if (returnUser is not null)
        {
            um.FindByIdAsync(returnUser.Id.ToString()).Returns(returnUser);
            um.GetRolesAsync(returnUser).Returns(new List<string>());
        }
        else
        {
            um.FindByIdAsync(Arg.Any<string>()).Returns((AppUser?)null);
        }

        return new GetUserPermissionsHandler(_factory.CreateContext(), um, cu);
    }

    // ── S1: Returns role names and permission codes ───────────────────────

    [Fact]
    public async Task Returns_role_names_and_permission_codes_from_roles()
    {
        // Seed role + permission + role_permission link
        await using var seed = _factory.CreateContext();
        var role = AppRole.Create("SalesManager", "Sales Manager");
        seed.Roles.Add(role);
        var permission = Permission.Create("lead:dispatch", "Dispatch Lead", "Leads", "dispatch");
        seed.Permissions.Add(permission);
        seed.RolePermissions.Add(RolePermission.Grant(role.Id, permission.Id));
        await seed.SaveChangesAsync();

        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Samba Diallo", "samba@test.sn");

        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        um.FindByIdAsync(user.Id.ToString()).Returns(user);
        um.GetRolesAsync(user).Returns(new List<string> { "SalesManager" });

        var handler = new GetUserPermissionsHandler(_factory.CreateContext(), um, cu);
        var result = await handler.Handle(new GetUserPermissionsQuery(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.RoleNames.Should().Contain("SalesManager");
        result.Value.RolePermissionCodes.Should().Contain("lead:dispatch");
    }

    // ── S2: Includes active non-expired scoped permissions ───────────────

    [Fact]
    public async Task Includes_active_non_expired_scoped_permissions()
    {
        var userId = Guid.NewGuid();

        await using var seed = _factory.CreateContext();
        var attribution = PermissionAttribution.Create(
            tenantId: _tenantId,
            userId: userId,
            permissionCode: "agency:supervision",
            assignedByUserId: Guid.NewGuid(),
            startDate: DateTimeOffset.UtcNow.AddDays(-1),
            endDate: DateTimeOffset.UtcNow.AddDays(30),
            scopeId: Guid.NewGuid(),
            scopeType: "Agency");
        seed.PermissionAttributions.Add(attribution);
        await seed.SaveChangesAsync();

        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Aissatou Ba", "aissatou@test.sn");
        typeof(AppUser).GetProperty(nameof(AppUser.Id))!.SetValue(user, userId);

        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        um.FindByIdAsync(userId.ToString()).Returns(user);
        um.GetRolesAsync(user).Returns(new List<string>());

        var handler = new GetUserPermissionsHandler(_factory.CreateContext(), um, cu);
        var result = await handler.Handle(new GetUserPermissionsQuery(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ScopedPermissions.Should().ContainSingle(p => p.PermissionCode == "agency:supervision");
    }

    // ── S3: Excludes expired scoped permissions ───────────────────────────

    [Fact]
    public async Task Excludes_expired_scoped_permissions()
    {
        var userId = Guid.NewGuid();

        await using var seed = _factory.CreateContext();
        var expired = PermissionAttribution.Create(
            tenantId: _tenantId,
            userId: userId,
            permissionCode: "old:permission",
            assignedByUserId: Guid.NewGuid(),
            startDate: DateTimeOffset.UtcNow.AddDays(-10),
            endDate: DateTimeOffset.UtcNow.AddDays(-1)); // already expired
        seed.PermissionAttributions.Add(expired);
        await seed.SaveChangesAsync();

        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Test User", "test@test.sn");
        typeof(AppUser).GetProperty(nameof(AppUser.Id))!.SetValue(user, userId);

        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        um.FindByIdAsync(userId.ToString()).Returns(user);
        um.GetRolesAsync(user).Returns(new List<string>());

        var handler = new GetUserPermissionsHandler(_factory.CreateContext(), um, cu);
        var result = await handler.Handle(new GetUserPermissionsQuery(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ScopedPermissions.Should().BeEmpty();
    }

    // ── S4: User not found ────────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_user_not_found()
    {
        var handler = BuildHandler(returnUser: null);
        var result = await handler.Handle(new GetUserPermissionsQuery(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S5: Tenant isolation ──────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_user_belongs_to_different_tenant()
    {
        var user = AppUser.Create(Guid.NewGuid(), Guid.NewGuid(), "Foreign", "foreign@test.sn");

        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId); // caller's tenant ≠ user's tenant
        um.FindByIdAsync(user.Id.ToString()).Returns(user);

        var handler = new GetUserPermissionsHandler(_factory.CreateContext(), um, cu);
        var result = await handler.Handle(new GetUserPermissionsQuery(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }
}
