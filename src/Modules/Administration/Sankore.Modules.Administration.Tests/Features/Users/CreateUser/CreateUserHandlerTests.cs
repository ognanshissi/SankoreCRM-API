namespace Sankore.Modules.Administration.Tests.Features.Users.CreateUser;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Modules.Administration.Domain.Events;
using Sankore.Modules.Administration.Features.Users.CreateUser;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Verifies CreateUserHandler against a real EF InMemory DbContext (so
/// query filters and entity persistence are genuinely exercised) while
/// mocking the Identity managers and IEventPublisher — the three external
/// seams the slice was designed around.
/// </summary>
public sealed class CreateUserHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public CreateUserHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    // ── S1: Happy-path — user created, event published ────────────────────

    [Fact]
    public async Task Should_create_user_and_publish_user_created_event()
    {
        // ARRANGE
        await using var db = _factory.CreateContext();

        var agency = Agency.Create(_tenantId, "Agence Dakar", "HQ", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(agency);
        await db.SaveChangesAsync();

        var roleId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();

        var userManager = IdentityMockFactory.BuildUserManager();
        var roleManager = IdentityMockFactory.BuildRoleManager();
        var publisher = Substitute.For<IEventPublisher>();
        var tenantContext = Substitute.For<ITenantContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        tenantContext.CurrentTenantId.Returns(_tenantId);
        currentUser.Id.Returns(callerUserId);

        var role = AppRole.Create("Agent", "Agent", isSystem: false);
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);

        roleManager.FindByIdAsync(roleId.ToString())
            .Returns(role);

        userManager.CreateAsync(Arg.Any<AppUser>())
            .Returns(IdentityResult.Success);

        userManager.AddToRoleAsync(Arg.Any<AppUser>(), role.Name!)
            .Returns(IdentityResult.Success);

        userManager.GeneratePasswordResetTokenAsync(Arg.Any<AppUser>())
            .Returns("activation-token");

        var notifications = Substitute.For<INotificationsModule>();
        notifications.QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result<Guid>.Ok(Guid.NewGuid()));

        var handler = new CreateUserHandler(db, userManager, roleManager, tenantContext, publisher, notifications);

        var command = new CreateUserCommand(
            AgencyId: agency.Id,
            RoleId: roleId,
            FullName: "Aminata Diallo",
            Email: "aminata@sankorefinance.sn",
            DefaultLanguage: "fr",
            SpokenLanguages: ["Wolof", "FR"],
            Specialties: ["Crédit individuel"],
            CallerUserId: callerUserId);

        // ACT
        var result = await handler.Handle(command, CancellationToken.None);

        // ASSERT
        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().NotBe(Guid.Empty);

        await publisher.Received(1).PublishAsync(
            Arg.Is<UserCreatedEvent>(e =>
                e.TenantId == _tenantId &&
                e.Email == command.Email &&
                e.FullName == command.FullName),
            Arg.Any<CancellationToken>());

        // Profile and custom UserRole row persisted
        await using var verify = _factory.CreateContext();
        verify.UserProfiles.Should().ContainSingle(p => p.UserId == result.Value.UserId);
    }

    // ── Guard: agency not found ───────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_agency_does_not_exist()
    {
        await using var db = _factory.CreateContext();

        var userManager = IdentityMockFactory.BuildUserManager();
        var roleManager = IdentityMockFactory.BuildRoleManager();
        var publisher = Substitute.For<IEventPublisher>();
        var tenantContext = Substitute.For<ITenantContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        tenantContext.CurrentTenantId.Returns(_tenantId);

        var notifications = Substitute.For<INotificationsModule>();
        var handler = new CreateUserHandler(db, userManager, roleManager, tenantContext, publisher, notifications);

        var command = new CreateUserCommand(
            AgencyId: Guid.NewGuid(), // no agency seeded
            RoleId: Guid.NewGuid(),
            FullName: "Test User",
            Email: "test@example.com",
            DefaultLanguage: "fr",
            SpokenLanguages: [],
            Specialties: [],
            CallerUserId: Guid.NewGuid());

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
        await publisher.DidNotReceive().PublishAsync(Arg.Any<UserCreatedEvent>(), Arg.Any<CancellationToken>());
    }

    // ── Guard: duplicate email within tenant ──────────────────────────────

    [Fact]
    public async Task Should_fail_when_email_already_taken_in_tenant()
    {
        // ARRANGE — seed an agency and an existing user with the same email
        await using var db = _factory.CreateContext();

        var agency = Agency.Create(_tenantId, "Agence Test", "", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(agency);

        var existingUser = AppUser.Create(_tenantId, agency.Id, "Existing User", "taken@test.com");
        existingUser.NormalizedEmail = "TAKEN@TEST.COM";
        db.Users.Add(existingUser);
        await db.SaveChangesAsync();

        var roleId = Guid.NewGuid();
        var userManager = IdentityMockFactory.BuildUserManager();
        var roleManager = IdentityMockFactory.BuildRoleManager();
        var publisher = Substitute.For<IEventPublisher>();

        var role = AppRole.Create("Agent", "Agent", isSystem: false);
        typeof(AppRole).GetProperty(nameof(AppRole.Id))!.SetValue(role, roleId);
        roleManager.FindByIdAsync(roleId.ToString()).Returns(role);

        var tenantContext = Substitute.For<ITenantContext>();
        var currentUser = Substitute.For<ICurrentUser>();
        tenantContext.CurrentTenantId.Returns(_tenantId);

        var notifications = Substitute.For<INotificationsModule>();
        var handler = new CreateUserHandler(db, userManager, roleManager, tenantContext, publisher, notifications);

        var command = new CreateUserCommand(
            AgencyId: agency.Id,
            RoleId: roleId,
            FullName: "Duplicate User",
            Email: "taken@test.com", // same email
            DefaultLanguage: "fr",
            SpokenLanguages: [],
            Specialties: [],
            CallerUserId: Guid.NewGuid());

        // ACT
        var result = await handler.Handle(command, CancellationToken.None);

        // ASSERT
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already exists");
        await publisher.DidNotReceive().PublishAsync(Arg.Any<UserCreatedEvent>(), Arg.Any<CancellationToken>());
    }
}
