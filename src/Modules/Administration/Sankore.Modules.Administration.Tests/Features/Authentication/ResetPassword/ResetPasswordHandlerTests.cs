using Microsoft.EntityFrameworkCore;

namespace Sankore.Modules.Administration.Tests.Features.Authentication.ResetPassword;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.ResetPassword;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class ResetPasswordHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ResetPasswordHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
    }

    public void Dispose() => _factory.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<AppUser> SeedActiveUserAsync(string email = "user@test.sn")
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "HQ", "HQ", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Amadou Ba", email);
        user.NormalizedEmail = email.ToUpperInvariant();
        user.Activate();

        // Set a non-null hash so PasswordHistory.Create succeeds in the handler
        typeof(Microsoft.AspNetCore.Identity.IdentityUser<Guid>)
            .GetProperty(nameof(user.PasswordHash))!
            .SetValue(user, "current-hash");

        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user;
    }

    private static ResetPasswordCommand ValidCommand(string userId, string token = "valid-token") =>
        new(userId, token, "NewSecurePass1!", "NewSecurePass1!");

    // ── S1: happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task Should_reset_password_archive_hash_and_extend_expiry_on_success()
    {
        var user = await SeedActiveUserAsync();
        var originalExpiresAt = user.PasswordExpiresAt;

        var userManager = IdentityMockFactory.BuildUserManager();
        userManager.ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var result = await handler.Handle(ValidCommand(user.Id.ToString()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Message.Should().NotBeNullOrEmpty();

        await using var verify = _factory.CreateContext();

        // Password history entry recorded
        verify.PasswordHistories
            .Where(p => p.UserId == user.Id)
            .Should().ContainSingle();

        // Expiry extended beyond original
        var saved = verify.Users.IgnoreQueryFilters().Single(u => u.Id == user.Id);
        saved.PasswordExpiresAt.Should().BeAfter(originalExpiresAt);
    }

    // ── Guard: invalid GUID ───────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_id_is_not_a_valid_guid()
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var cmd = new ResetPasswordCommand("not-a-guid", "token", "Pass1!", "Pass1!");
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
    }

    // ── Guard: user not found ─────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_does_not_exist()
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var result = await handler.Handle(ValidCommand(Guid.NewGuid().ToString()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
    }

    // ── Guard: non-Active user (PendingActivation) ────────────────────────

    [Fact]
    public async Task Should_fail_when_user_is_pending_activation()
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "HQ", "HQ", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Pending User", "pending@test.sn");
        user.NormalizedEmail = "PENDING@TEST.SN";
        // Status remains PendingActivation (not activated)
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var result = await handler.Handle(ValidCommand(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await userManager.DidNotReceive()
            .ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── Guard: non-Active user (Disabled) ────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_is_disabled()
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "HQ", "HQ", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Disabled User", "disabled@test.sn");
        user.NormalizedEmail = "DISABLED@TEST.SN";
        user.Activate();
        user.Deactivate();
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var result = await handler.Handle(ValidCommand(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        await userManager.DidNotReceive()
            .ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── Guard: invalid token rejected by Identity ─────────────────────────

    [Fact]
    public async Task Should_fail_when_identity_rejects_the_token()
    {
        var user = await SeedActiveUserAsync("active@test.sn");

        var userManager = IdentityMockFactory.BuildUserManager();
        userManager.ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "InvalidToken", Description = "Invalid token." }));

        await using var db = _factory.CreateContext();
        var handler = new ResetPasswordHandler(db, userManager);

        var result = await handler.Handle(ValidCommand(user.Id.ToString(), "bad-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid token");

        // No history entry recorded, expiry unchanged
        await using var verify = _factory.CreateContext();
        verify.PasswordHistories.Where(p => p.UserId == user.Id).Should().BeEmpty();
    }
}
