namespace Sankore.Modules.Administration.Tests.Features.Users.ReactivateUser;

using FluentAssertions;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.ReactivateUser;
using Sankore.Modules.Administration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Reactivation restores Status and deliberately nothing else. These tests exist to pin that
/// choice: the asymmetry with DeactivateUserHandler (which revokes role grants in both
/// Identity and the db.UserRoles audit mirror) is the decision, not an oversight, and a future
/// reader "restoring the symmetry" by re-granting roles here would hand an account its old
/// permissions back without anyone deciding to.
/// </summary>
public sealed class ReactivateUserHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;

    public ReactivateUserHandlerTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    private async Task<AppUser> SeedDisabledUserWithRevokedRoleAsync()
    {
        await using var seed = _factory.CreateContext();

        var agency = Agency.Create(
            _tenantId, "HQ0010", "Agence HQ", "", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);

        var user = AppUser.Create(_tenantId, agency.Id, "Salif", "Traore", "salif@test.sn");
        user.Activate();
        user.Deactivate();
        seed.Users.Add(user);

        // The mirror row deactivation left behind: revoked, and indistinguishable from one an
        // administrator revoked on purpose through RevokeRole.
        var grant = UserRole.Assign(_tenantId, user.Id, Guid.NewGuid(), Guid.NewGuid());
        grant.Revoke();
        seed.Set<UserRole>().Add(grant);

        await seed.SaveChangesAsync();
        return user;
    }

    [Fact]
    public async Task Restores_the_status_so_the_account_can_sign_in_again()
    {
        var user = await SeedDisabledUserWithRevokedRoleAsync();

        await using var db = _factory.CreateContext();
        var result = await new ReactivateUserHandler(db)
            .Handle(new ReactivateUserCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        var saved = verify.Users.Single(u => u.Id == user.Id);
        saved.Status.Should().Be(UserStatus.Active);
        saved.DeactivatedAt.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_restore_the_roles_deactivation_revoked()
    {
        var user = await SeedDisabledUserWithRevokedRoleAsync();

        await using var db = _factory.CreateContext();
        var result = await new ReactivateUserHandler(db)
            .Handle(new ReactivateUserCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();

        await using var verify = _factory.CreateContext();
        verify.Set<UserRole>()
            .Where(r => r.UserId == user.Id)
            .Should().AllSatisfy(r => r.IsActive.Should().BeFalse(
                "a reactivated account comes back with no roles until an administrator grants "
                + "them again — restoring blindly cannot tell a deactivation's revocation from "
                + "one an administrator made deliberately"));
    }

    [Fact]
    public async Task Fails_when_the_user_is_not_disabled()
    {
        await using var seed = _factory.CreateContext();
        var agency = Agency.Create(
            _tenantId, "HQ0011", "Agence HQ", "", AgencyType.HeadQuarter, null, null);
        seed.Agencies.Add(agency);
        var user = AppUser.Create(_tenantId, agency.Id, "Kadia", "Sylla", "kadia@test.sn");
        user.Activate();
        seed.Users.Add(user);
        await seed.SaveChangesAsync();

        await using var db = _factory.CreateContext();
        var result = await new ReactivateUserHandler(db)
            .Handle(new ReactivateUserCommand(user.Id), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_when_the_user_does_not_exist()
    {
        await using var db = _factory.CreateContext();
        var result = await new ReactivateUserHandler(db)
            .Handle(new ReactivateUserCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not found");
    }
}
