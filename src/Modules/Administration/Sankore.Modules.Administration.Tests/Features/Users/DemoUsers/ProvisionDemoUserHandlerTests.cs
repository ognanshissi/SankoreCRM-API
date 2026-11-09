namespace Sankore.Modules.Administration.Tests.Features.Users.DemoUsers;

using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.StartingData;
using Sankore.Modules.Administration.Features.Users.DemoUsers;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// A demo account (docs/donnees-demarrage-demo.md, lot 2): active at once with the shared password,
/// no activation e-mail — and only in a test institution, never a twin, only at an address of the demo template.
/// </summary>
public sealed class ProvisionDemoUserHandlerTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId;
    private readonly TestAdminDbContextFactory _factory;
    private readonly ITenantStore _tenants = Substitute.For<ITenantStore>();
    private readonly UserManager<AppUser> _users = IdentityMockFactory.BuildUserManager();
    private readonly RoleManager<AppRole> _roles = IdentityMockFactory.BuildRoleManager();

    public ProvisionDemoUserHandlerTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        using (var db = _factory.CreateContext())
        {
            var agency = Agency.Create(_tenantId, "AG0001", "Agence Plateau", "", AgencyType.HeadQuarter, null, null);
            db.Agencies.Add(agency);
            db.SaveChanges();
            _agencyId = agency.Id;
        }
        Tenant(isTest: true);
        _roles.FindByNameAsync("Agent").Returns(AppRole.Create("Agent", "Compte Agent", isSystem: true));
        _users.CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(ci =>
        {
            ((AppUser)ci[0]).PasswordHash = "hash";
            return IdentityResult.Success;
        });
        _users.AddToRoleAsync(Arg.Any<AppUser>(), Arg.Any<string>()).Returns(IdentityResult.Success);
    }

    public void Dispose() => _factory.Dispose();

    private void Tenant(bool isTest, Guid? parent = null)
        => _tenants.GetAsync(_tenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(_tenantId, "Démo", "demo.sankore.local", true, false, null, null, IsTest: isTest, ParentTenantId: parent));

    private async Task<Result<Guid>> SendAsync(string email = "contact+agent-plateau-1.demo@talisoft.net")
    {
        var db = _factory.CreateContext();
        return await new ProvisionDemoUserHandler(db, _users, _roles, new FixedTenantContext(_tenantId), _tenants, Options.Create(new DemoDataOptions()),
                Substitute.For<IEventPublisher>(), new EmployeeNumberAllocator(db))
            .Handle(new ProvisionDemoUserCommand(_agencyId, "Agent", "Fatou", "Diallo", email, "Demo-2026!", Guid.NewGuid()), default);
    }

    [Fact]
    public async Task The_account_is_active_with_a_password_that_does_not_expire_now()
    {
        var created = await SendAsync();

        created.IsSuccess.Should().BeTrue(created.Error);
        await _users.Received(1).CreateAsync(
            Arg.Is<AppUser>(u => u.Status == UserStatus.Active && u.PasswordExpiresAt > DateTimeOffset.UtcNow.AddDays(1)),
            "Demo-2026!");
        await using var db = _factory.CreateContext();
        db.UserRoles.Should().ContainSingle();
        db.UserProfiles.Should().ContainSingle();
        db.PasswordHistories.Should().ContainSingle();
    }

    [Fact]
    public async Task A_real_institution_or_a_twin_gets_no_demo_account()
    {
        Tenant(isTest: false);
        (await SendAsync()).Error.Should().Be(DemoUserErrors.NotTestTenant);

        Tenant(isTest: true, parent: Guid.NewGuid());
        (await SendAsync()).Error.Should().Be(DemoUserErrors.NotTestTenant);

        await _users.DidNotReceive().CreateAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData("fatou.diallo@gmail.com")]
    [InlineData("contact@talisoft.net")]
    [InlineData("direction+agent@talisoft.net")]
    [InlineData("contact+agent.demo@talisoft.net.evil.com")]
    [InlineData("contact+agent@talisoft.net")]
    public async Task Only_an_address_the_demo_template_produces(string email)
        => (await SendAsync(email)).Error.Should().Be(DemoUserErrors.NotDemoAddress);

    [Fact]
    public async Task An_address_another_institution_holds_is_refused_by_name_not_by_the_database()
    {
        // Identity's user-name index spans every tenant: the second demo must say so.
        await using (var db = _factory.CreateContext())
        {
            var other = AppUser.CreateRoot(Guid.NewGuid(), "Fatou", "Diallo", "contact+agent-plateau-1.demo@talisoft.net");
            other.NormalizedEmail = other.Email!.ToUpperInvariant();
            other.NormalizedUserName = other.NormalizedEmail;
            db.Users.Add(other);
            await db.SaveChangesAsync();
        }

        (await SendAsync()).Error.Should().Be(DemoUserErrors.TakenElsewhere);
    }
}
