namespace Sankore.Modules.Administration.Tests.Features.Users.AssignScopedPermission;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.AssignScopedPermission;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class AssignScopedPermissionHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public AssignScopedPermissionHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private static readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset _end = DateTimeOffset.UtcNow.AddMonths(3);

    private AssignScopedPermissionHandler BuildHandler(AppUser? user = null)
    {
        var um = IdentityMockFactory.BuildUserManager();
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        cu.Id.Returns(Guid.NewGuid());

        if (user is not null)
            um.FindByIdAsync(user.Id.ToString()).Returns(user);
        else
            um.FindByIdAsync(Arg.Any<string>()).Returns((AppUser?)null);

        return new AssignScopedPermissionHandler(_factory.CreateContext(), um, cu);
    }

    // ── S1: Happy path — attribution created, Id returned ────────────────

    [Fact]
    public async Task Creates_attribution_and_returns_its_id()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Mariama Bah", "mariama@test.sn");

        await using var seed = _factory.CreateContext();
        seed.Permissions.Add(Permission.Create("agency:read", "Read Agency", "Administration", "read"));
        await seed.SaveChangesAsync();

        var handler = BuildHandler(user);
        var cmd = new AssignScopedPermissionCommand(
            UserId: user.Id,
            PermissionCode: "agency:read",
            StartDate: _start,
            EndDate: _end);

        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBe(Guid.Empty);

        await using var verify = _factory.CreateContext();
        verify.PermissionAttributions.Should().ContainSingle(a =>
            a.UserId == user.Id &&
            a.PermissionCode == "agency:read" &&
            a.IsActive);
    }

    // ── S2: User not found ────────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_user_not_found()
    {
        var handler = BuildHandler(user: null);
        var result = await handler.Handle(
            new AssignScopedPermissionCommand(Guid.NewGuid(), "agency:read", _start, _end),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S3: Permission code not in catalogue ──────────────────────────────

    [Fact]
    public async Task Fails_when_permission_code_does_not_exist()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Test", "test@test.sn");
        var handler = BuildHandler(user);

        var result = await handler.Handle(
            new AssignScopedPermissionCommand(user.Id, "nonexistent:code", _start, _end),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("does not exist");
    }

    // ── S4: Duplicate active attribution rejected ─────────────────────────

    [Fact]
    public async Task Fails_when_duplicate_active_attribution_exists()
    {
        var userId = Guid.NewGuid();
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Dial Mbaye", "dial@test.sn");
        typeof(AppUser).GetProperty(nameof(AppUser.Id))!.SetValue(user, userId);

        await using var seed = _factory.CreateContext();
        seed.Permissions.Add(Permission.Create("agency:read", "Read Agency", "Administration", "read"));
        seed.PermissionAttributions.Add(PermissionAttribution.Create(
            _tenantId, userId, "agency:read", Guid.NewGuid(), _start, _end));
        await seed.SaveChangesAsync();

        var handler = BuildHandler(user);
        var result = await handler.Handle(
            new AssignScopedPermissionCommand(userId, "agency:read", _start, _end),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
    }

    // ── S5: Global attribution (no scope) accepted ───────────────────────

    [Fact]
    public async Task Accepts_global_attribution_with_null_scope()
    {
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Coumba Sarr", "coumba@test.sn");

        await using var seed = _factory.CreateContext();
        seed.Permissions.Add(Permission.Create("user:read", "Read User", "Administration", "read"));
        await seed.SaveChangesAsync();

        var handler = BuildHandler(user);
        var result = await handler.Handle(
            new AssignScopedPermissionCommand(
                UserId: user.Id,
                PermissionCode: "user:read",
                StartDate: _start,
                EndDate: _end,
                ScopeId: null,
                ScopeType: null),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        verify.PermissionAttributions.Should().ContainSingle(a =>
            a.UserId == user.Id && a.ScopeId == null && a.ScopeType == null);
    }
}
