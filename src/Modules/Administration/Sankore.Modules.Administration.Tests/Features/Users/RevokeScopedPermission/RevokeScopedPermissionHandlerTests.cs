namespace Sankore.Modules.Administration.Tests.Features.Users.RevokeScopedPermission;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.RevokeScopedPermission;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class RevokeScopedPermissionHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public RevokeScopedPermissionHandlerTests() => _factory = new(_tenantId);
    public void Dispose() => _factory.Dispose();

    private RevokeScopedPermissionHandler BuildHandler()
    {
        var cu = Substitute.For<ICurrentUser>();
        cu.TenantId.Returns(_tenantId);
        return new RevokeScopedPermissionHandler(_factory.CreateContext(), cu);
    }

    private PermissionAttribution SeedAttribution(Guid userId, bool active = true)
    {
        var a = PermissionAttribution.Create(
            tenantId: _tenantId,
            userId: userId,
            permissionCode: "agency:read",
            assignedByUserId: Guid.NewGuid(),
            startDate: DateTimeOffset.UtcNow.AddDays(-1),
            endDate: DateTimeOffset.UtcNow.AddDays(30));

        if (!active) a.Revoke();
        return a;
    }

    // ── S1: Happy path — IsActive flipped to false ────────────────────────

    [Fact]
    public async Task Revokes_attribution_and_sets_IsActive_false()
    {
        var userId = Guid.NewGuid();
        await using var seed = _factory.CreateContext();
        var attribution = SeedAttribution(userId);
        seed.PermissionAttributions.Add(attribution);
        await seed.SaveChangesAsync();

        var handler = BuildHandler();
        var result = await handler.Handle(
            new RevokeScopedPermissionCommand(userId, attribution.Id),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        var saved = verify.PermissionAttributions.Single(a => a.Id == attribution.Id);
        saved.IsActive.Should().BeFalse();
        saved.EndDate.Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(5));
    }

    // ── S2: Attribution not found ─────────────────────────────────────────

    [Fact]
    public async Task Fails_when_attribution_not_found()
    {
        var handler = BuildHandler();
        var result = await handler.Handle(
            new RevokeScopedPermissionCommand(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }

    // ── S3: Already revoked ───────────────────────────────────────────────

    [Fact]
    public async Task Fails_when_attribution_already_revoked()
    {
        var userId = Guid.NewGuid();
        await using var seed = _factory.CreateContext();
        var attribution = SeedAttribution(userId, active: false); // already revoked
        seed.PermissionAttributions.Add(attribution);
        await seed.SaveChangesAsync();

        var handler = BuildHandler();
        var result = await handler.Handle(
            new RevokeScopedPermissionCommand(userId, attribution.Id),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already revoked");
    }

    // ── S4: Attribution belonging to different user is not found ──────────

    [Fact]
    public async Task Fails_when_attribution_belongs_to_different_user()
    {
        var realOwnerId = Guid.NewGuid();
        var requestingUserId = Guid.NewGuid(); // different user

        await using var seed = _factory.CreateContext();
        var attribution = SeedAttribution(realOwnerId);
        seed.PermissionAttributions.Add(attribution);
        await seed.SaveChangesAsync();

        var handler = BuildHandler();
        var result = await handler.Handle(
            new RevokeScopedPermissionCommand(requestingUserId, attribution.Id), // wrong userId
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }
}
