namespace Sankore.Modules.Administration.Tests.Features.Users.DeactivateUser;

using FluentAssertions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Domain.Events;
using Sankore.Modules.Administration.Features.Users.DeactivateUser;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// Verifies DeactivateUserHandler — domain state transitions, role revocation,
/// event publishing, and idempotency guard (double-deactivation rejected).
/// </summary>
public sealed class DeactivateUserHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public DeactivateUserHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    // ── S3: Active user → Disabled ────────────────────────────────────────

    [Fact]
    public async Task Should_set_status_to_disabled_revoke_roles_and_publish_event()
    {
        // ARRANGE
        await using var seed = _factory.CreateContext();

        var agency = Agency.Create(_tenantId, "Agence HQ", "", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Moussa Sow", "moussa@test.sn");
        user.Activate();
        seed.Users.Add(user);

        var userRole = UserRole.Assign(_tenantId, user.Id, Guid.NewGuid(), Guid.NewGuid());
        seed.Set<UserRole>().Add(userRole);

        await seed.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        await using var db = _factory.CreateContext();
        var handler = new DeactivateUserHandler(db, publisher);

        // ACT
        var result = await handler.Handle(new DeactivateUserCommand(user.Id), CancellationToken.None);

        // ASSERT
        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var saved = verify.Users.Single(u => u.Id == user.Id);
        saved.Status.Should().Be(UserStatus.Disabled);
        saved.DeactivatedAt.Should().NotBeNull();

        // All active role assignments revoked
        var roles = verify.Set<UserRole>().Where(r => r.UserId == user.Id).ToList();
        roles.Should().NotBeEmpty();
        roles.Should().AllSatisfy(r => r.IsActive.Should().BeFalse());

        await publisher.Received(1).PublishAsync(
            Arg.Is<UserDeactivatedEvent>(e => e.UserId == user.Id && e.TenantId == _tenantId),
            Arg.Any<CancellationToken>());
    }

    // ── Guard: user not found ─────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_does_not_exist()
    {
        var publisher = Substitute.For<IEventPublisher>();
        await using var db = _factory.CreateContext();
        var handler = new DeactivateUserHandler(db, publisher);

        var result = await handler.Handle(
            new DeactivateUserCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
        await publisher.DidNotReceive().PublishAsync(
            Arg.Any<UserDeactivatedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Guard: idempotency — double deactivation rejected ────────────────

    [Fact]
    public async Task Should_fail_when_user_is_already_disabled()
    {
        // ARRANGE — seed a user already in Disabled state
        await using var seed = _factory.CreateContext();

        var agency = Agency.Create(_tenantId, "Agence Test", "", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Fatou Ndiaye", "fatou@test.sn");
        user.Activate();
        user.Deactivate(); // put in Disabled state
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        await using var db = _factory.CreateContext();
        var handler = new DeactivateUserHandler(db, publisher);

        // ACT
        var result = await handler.Handle(new DeactivateUserCommand(user.Id), CancellationToken.None);

        // ASSERT
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already disabled");
        await publisher.DidNotReceive().PublishAsync(
            Arg.Any<UserDeactivatedEvent>(), Arg.Any<CancellationToken>());
    }
}
