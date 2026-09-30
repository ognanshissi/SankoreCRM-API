namespace Sankore.Modules.Administration.Tests.Features.Authentication.ResetPassword;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.ResetPassword;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Checks a reset link before the user types anything. Without it they fill the form twice,
/// submit, and only then learn the link expired — with the token spent either way.
/// </summary>
public sealed class ValidateResetTokenHandlerTests : IDisposable
{
    private const string ValidToken = "valid-token";
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ValidateResetTokenHandlerTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private async Task<AppUser> SeedUserAsync(bool active = true, Guid? tenantId = null)
    {
        await using var seed = _factory.CreateContext();
        var tenant = tenantId ?? _tenantId;

        var agency = Agency.Create(tenant, $"HQ{Guid.NewGuid():N}"[..8], "HQ", "HQ",
            AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(tenant, agency.Id, "Amadou", "Johnson",
            $"{Guid.NewGuid():N}@test.ci");
        if (active) user.Activate();

        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user;
    }

    /// <summary>A UserManager that accepts exactly <see cref="ValidToken"/>.</summary>
    private static UserManager<AppUser> UserManagerAccepting(string token = ValidToken)
    {
        var userManager = IdentityMockFactory.BuildUserManager();
        userManager.VerifyUserTokenAsync(
                Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => (string)ci[3] == token);
        return userManager;
    }

    private async Task<Sankore.Shared.Kernel.Result> ValidateAsync(
        string userId, string token, UserManager<AppUser>? userManager = null)
    {
        await using var db = _factory.CreateContext();
        var handler = new ValidateResetTokenHandler(db, userManager ?? UserManagerAccepting());
        return await handler.Handle(new ValidateResetTokenQuery(userId, token), CancellationToken.None);
    }

    [Fact]
    public async Task Accepts_a_valid_token_for_an_active_user()
    {
        var user = await SeedUserAsync();

        var result = await ValidateAsync(user.Id.ToString(), ValidToken);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_an_expired_or_already_used_token()
    {
        var user = await SeedUserAsync();

        var result = await ValidateAsync(user.Id.ToString(), "stale-token");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("invalid or has expired");
    }

    [Fact]
    public async Task Never_consumes_the_token()
    {
        // The whole point: the user must still be able to submit the form afterwards.
        var user = await SeedUserAsync();
        var userManager = UserManagerAccepting();

        await ValidateAsync(user.Id.ToString(), ValidToken, userManager);

        await userManager.DidNotReceive().ResetPasswordAsync(
            Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task Asks_Identity_with_the_same_provider_and_purpose_the_reset_uses()
    {
        // A mismatch here reads as an expired link rather than as a bug — the hardest kind of
        // defect to trace in a flow nobody can reproduce on demand.
        var user = await SeedUserAsync();
        var userManager = UserManagerAccepting();

        await ValidateAsync(user.Id.ToString(), ValidToken, userManager);

        await userManager.Received(1).VerifyUserTokenAsync(
            Arg.Is<AppUser>(u => u.Id == user.Id),
            userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<AppUser>.ResetPasswordTokenPurpose,
            ValidToken);
    }

    [Fact]
    public async Task Rejects_a_user_id_that_is_not_a_guid()
    {
        var result = await ValidateAsync("not-a-guid", ValidToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("invalid or has expired");
    }

    [Fact]
    public async Task An_unknown_user_is_indistinguishable_from_a_bad_token()
    {
        // Otherwise this anonymous endpoint becomes an account-enumeration oracle.
        var unknown = await ValidateAsync(Guid.NewGuid().ToString(), ValidToken);
        var badToken = await ValidateAsync((await SeedUserAsync()).Id.ToString(), "stale-token");

        unknown.IsFailure.Should().BeTrue();
        unknown.Error.Should().Be(badToken.Error);
    }

    [Fact]
    public async Task A_user_who_never_activated_is_sent_to_the_activation_flow()
    {
        var user = await SeedUserAsync(active: false);

        var result = await ValidateAsync(user.Id.ToString(), ValidToken);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not available for this account");
    }

    [Fact]
    public async Task A_link_for_another_tenant_still_validates()
    {
        // Deliberate, and it mirrors ResetPasswordHandler: the link arrives by email and carries
        // no JWT. If this query filtered by tenant it would reject links the reset then accepts,
        // and its whole purpose is to predict what the reset will do.
        var user = await SeedUserAsync(tenantId: Guid.NewGuid());

        var result = await ValidateAsync(user.Id.ToString(), ValidToken);

        result.IsSuccess.Should().BeTrue();
    }
}
