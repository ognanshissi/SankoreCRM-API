using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Tests.Features.Authentication.AccountActivation;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.AccountActivation;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

public sealed class AccountActivationHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ITenantContext _context;

    public AccountActivationHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _context = Substitute.For<ITenantContext>();
    }

    public void Dispose() => _factory.Dispose();

    // ── helpers ───────────────────────────────────────────────────────────

    private async Task<AppUser> SeedPendingUserAsync(string email = "user@test.sn")
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "HQ0001", "HQ", "HQ", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Aminata","Diallo", email);
        user.NormalizedEmail = email.ToUpperInvariant();

        // Set a non-null hash so PasswordHistory.Create succeeds in the handler
        typeof(Microsoft.AspNetCore.Identity.IdentityUser<Guid>)
            .GetProperty(nameof(user.PasswordHash))!
            .SetValue(user, "placeholder-hash");

        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user;
    }

    private static AccountActivationCommand ValidCommand(Guid userId, string token = "valid-token") =>
        new(userId.ToString(), token, "SecurePass1!", "SecurePass1!");

    /// <summary>
    /// Activation tokens live in their own provider, so the handler verifies explicitly and only
    /// then sets the password with an internal reset token. Both seams have to be stubbed.
    /// </summary>
    private static void StubTokenAccepted(UserManager<AppUser> userManager, bool accepted = true)
    {
        userManager.VerifyUserTokenAsync(
                Arg.Any<AppUser>(),
                ActivationTokens.ProviderName,
                ActivationTokens.Purpose,
                Arg.Any<string>())
            .Returns(accepted);

        userManager.GeneratePasswordResetTokenAsync(Arg.Any<AppUser>())
            .Returns("internal-reset-token");
    }

    // ── S1: happy path ────────────────────────────────────────────────────

    [Fact]
    public async Task Should_activate_user_and_set_password_when_token_is_valid()
    {
        var user = await SeedPendingUserAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        StubTokenAccepted(userManager);
        userManager.ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(IdentityResult.Success);

        await using var db = _factory.CreateContext();
        var handler = new AccountActivationHandler(db, userManager, _context);

        var result = await handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Success.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        verify.Users.IgnoreQueryFilters()
            .Single(u => u.Id == user.Id)
            .Status.Should().Be(UserStatus.Active);
    }

    // ── Guard: invalid GUID ───────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_id_is_not_a_valid_guid()
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new AccountActivationHandler(db, userManager,  _context);

        var cmd = new AccountActivationCommand("not-a-guid", "token", "Pass1!", "Pass1!");
        var result = await handler.Handle(cmd, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
    }

    // ── Guard: user not found ─────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_user_id_does_not_exist()
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new AccountActivationHandler(db, userManager,  _context);

        var result = await handler.Handle(ValidCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("Invalid");
    }

    // ── Guard: already active ─────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_account_is_already_active()
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(_tenantId, "HQ0001", "HQ", "HQ", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Moussa", "Sow", "active@test.sn");
        user.NormalizedEmail = "ACTIVE@TEST.SN";
        user.Activate();
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        var userManager = IdentityMockFactory.BuildUserManager();
        await using var db = _factory.CreateContext();
        var handler = new AccountActivationHandler(db, userManager, _context);

        var result = await handler.Handle(ValidCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("already active");
        await userManager.DidNotReceive()
            .ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    // ── Guard: invalid token ──────────────────────────────────────────────

    [Fact]
    public async Task Should_fail_when_identity_rejects_the_token()
    {
        var user = await SeedPendingUserAsync("pending@test.sn");

        var userManager = IdentityMockFactory.BuildUserManager();
        StubTokenAccepted(userManager, accepted: false);

        await using var db = _factory.CreateContext();
        var handler = new AccountActivationHandler(db, userManager,  _context);

        var result = await handler.Handle(
            ValidCommand(user.Id,  "bad-token"), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("expired or already been used");

        // A rejected token must never reach the password pipeline.
        await userManager.DidNotReceive()
            .ResetPasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());

        // Status must remain PendingActivation
        await using var verify = _factory.CreateContext();
        verify.Users.IgnoreQueryFilters()
            .Single(u => u.Id == user.Id)
            .Status.Should().Be(UserStatus.PendingActivation);
    }
}
