namespace Sankore.Modules.Administration.Tests.Features.Users.ResetPassword;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.ResetPassword;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Verifies ResetPasswordHandler — password-history reuse detection,
/// old-hash archival, expiry extension, and the 12-entry cap.
/// Uses a real EF InMemory DbContext for data persistence, a mocked
/// UserManager (so no real token round-trip is required in unit tests),
/// and a mocked IPasswordHasher to control verification outcomes.
/// </summary>
public sealed class ResetPasswordHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ResetPasswordHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    // ── S2a: New password matches a historic hash — rejected ─────────────

    [Fact]
    public async Task Should_reject_password_that_matches_a_historic_hash()
    {
        // ARRANGE
        await using var seed = _factory.CreateContext();
        var (user, _) = await SeedActiveUserAsync(seed);

        seed.PasswordHistories.Add(
            PasswordHistory.Create(_tenantId, user.Id, "hashed-old-password"));
        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        var hasher = Substitute.For<IPasswordHasher<AppUser>>();

        // Simulate: the new password matches the archived hash
        hasher.VerifyHashedPassword(
                Arg.Any<AppUser>(), "hashed-old-password", Arg.Any<string>())
            .Returns(PasswordVerificationResult.Success);

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager, hasher);

        // ACT
        var result = await handler.Handle(
            new ResetPasswordCommand(user.Id, "NewPassword1!"), CancellationToken.None);

        // ASSERT
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("PASSWORD_RECENTLY_USED");
        await userManager.DidNotReceive()
            .GeneratePasswordResetTokenAsync(Arg.Any<AppUser>());
    }

    // ── S2b: Valid new password — old hash archived, expiry extended ──────

    [Fact]
    public async Task Should_archive_old_hash_and_extend_expiry_on_success()
    {
        // ARRANGE
        await using var seed = _factory.CreateContext();
        var (user, oldExpiresAt) = await SeedActiveUserAsync(seed, passwordHash: "current-hash");

        var userManager = IdentityMockFactory.BuildUserManager();
        var hasher = Substitute.For<IPasswordHasher<AppUser>>();

        // No historic hashes match → allow the reset
        hasher.VerifyHashedPassword(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(PasswordVerificationResult.Failed);

        userManager.GeneratePasswordResetTokenAsync(Arg.Any<AppUser>())
            .Returns("reset-token");

        userManager.ResetPasswordAsync(Arg.Any<AppUser>(), "reset-token", "NewPass1!")
            .Returns(IdentityResult.Success);

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager, hasher);

        // ACT
        var result = await handler.Handle(
            new ResetPasswordCommand(user.Id, "NewPass1!"), CancellationToken.None);

        // ASSERT
        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();

        // Old hash archived
        verify.PasswordHistories
            .Where(p => p.UserId == user.Id)
            .Should().ContainSingle(p => p.PasswordHash == "current-hash");

        // PasswordExpiresAt extended beyond the original value
        var savedUser = verify.Users.Single(u => u.Id == user.Id);
        savedUser.PasswordExpiresAt.Should().BeAfter(oldExpiresAt);
    }

    // ── S2c: History check capped at 12 entries ───────────────────────────

    [Fact]
    public async Task Should_only_check_last_12_hashes_from_history()
    {
        // ARRANGE — seed 14 history records; only the 12 most recent are checked
        await using var seed = _factory.CreateContext();
        var (user, _) = await SeedActiveUserAsync(seed);

        // Oldest 2 entries — these MUST NOT be checked (beyond the 12-entry cap)
        for (var i = 0; i < 2; i++)
        {
            var entry = PasswordHistory.Create(_tenantId, user.Id, $"old-hash-{i}");
            seed.PasswordHistories.Add(entry);
        }

        // Most-recent 12 entries (all will be verified)
        for (var i = 0; i < 12; i++)
        {
            var entry = PasswordHistory.Create(_tenantId, user.Id, $"recent-hash-{i}");
            seed.PasswordHistories.Add(entry);
        }

        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        var hasher = Substitute.For<IPasswordHasher<AppUser>>();

        // All "recent-hash-*" entries match → PASSWORD_RECENTLY_USED
        hasher.VerifyHashedPassword(
                Arg.Any<AppUser>(),
                Arg.Is<string>(h => h.StartsWith("recent-hash-")),
                Arg.Any<string>())
            .Returns(PasswordVerificationResult.Success);

        // "old-hash-*" entries should never be checked
        hasher.VerifyHashedPassword(
                Arg.Any<AppUser>(),
                Arg.Is<string>(h => h.StartsWith("old-hash-")),
                Arg.Any<string>())
            .Returns(PasswordVerificationResult.Failed);

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager, hasher);

        // ACT
        var result = await handler.Handle(
            new ResetPasswordCommand(user.Id, "NewPass1!"), CancellationToken.None);

        // ASSERT — handler stopped at a recent-hash match, never reached old hashes
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("PASSWORD_RECENTLY_USED");

        hasher.DidNotReceive().VerifyHashedPassword(
            Arg.Any<AppUser>(),
            Arg.Is<string>(h => h.StartsWith("old-hash-")),
            Arg.Any<string>());
    }

    // ── Guard: disabled user ──────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_is_disabled()
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "Agence HQ", "", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Locked User", "locked@test.sn");
        user.Activate();
        user.Deactivate(); // Disabled
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        var hasher = Substitute.For<IPasswordHasher<AppUser>>();

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager, hasher);

        var result = await handler.Handle(
            new ResetPasswordCommand(user.Id, "Irrelevant1!"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("disabled");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<(AppUser user, DateTimeOffset originalExpiresAt)> SeedActiveUserAsync(
        Infrastructure.AdministrationDbContext db,
        string? passwordHash = null)
    {
        var agency = Agency.Create(_tenantId, "Agence Seed", "", AgencyType.HeadQuarter, null, null);
        db.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Seed User", "seed@test.sn");
        user.Activate();

        // Directly set PasswordHash via reflection (bypasses the private setter)
        if (passwordHash is not null)
            typeof(Microsoft.AspNetCore.Identity.IdentityUser<Guid>)
                .GetProperty(nameof(user.PasswordHash))!
                .SetValue(user, passwordHash);

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user, user.PasswordExpiresAt);
    }
}
