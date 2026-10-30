namespace Sankore.Modules.Administration.Tests.Features.Authentication;

using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Authentication.RefreshToken;
using Sankore.Modules.Administration.Features.Authentication.TestMode;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.Infrastructure.PaidModules;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;
using Xunit;
using DomainRefreshToken = Sankore.Modules.Administration.Domain.RefreshToken;

/// <summary>
/// Sandbox lot 2, « Mode test » à la Stripe: the same account and session switch to the tenant's
/// test twin and back. The twin is created and seeded once, the token names it while the team
/// stays production's, the mode survives a refresh, and the team itself is shared.
/// </summary>
public sealed class SwitchTestModeTests : IDisposable
{
    private static readonly JwtOptions Jwt = new()
    {
        SigningKey = "a-test-signing-key-that-is-long-enough-for-hs256",
        Issuer = "sankore",
        Audience = "sankore",
    };

    private readonly Guid _live = Guid.NewGuid();
    private readonly Guid _twin = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly ITenantStore _store = Substitute.For<ITenantStore>();
    private readonly FakeTwinRegistry _registry = new();
    private readonly AppUser _user;
    private int _seeded;

    public SwitchTestModeTests()
    {
        _factory = new TestAdminDbContextFactory(_live);
        _user = AppUser.CreateRoot(_live, "Awa", "Koné", "awa@caisse.ci");
        _user.Activate();
        using var db = _factory.CreateContext();
        db.Users.Add(_user);
        db.SaveChanges();

        // Before the first switch the registry knows no twin; it answers one when asked.
        _store.GetAsync(_live, Arg.Any<CancellationToken>()).Returns(Live(testTenantId: null));
        _store.GetAsync(_twin, Arg.Any<CancellationToken>()).Returns(
            new TenantInfo(_twin, "Caisse (test)", "caisse.ci~test", true, false, null, null, IsTest: true, ParentTenantId: _live));
        _registry.Twin = _twin;
    }

    public void Dispose() => _factory.Dispose();

    private TenantInfo Live(Guid? testTenantId)
        => new(_live, "Caisse", "caisse.ci", true, false, null, null, TestTenantId: testTenantId);

    [Fact]
    public async Task Entering_creates_and_seeds_the_twin_once_and_the_token_names_it_beside_the_team()
    {
        var result = await SwitchAsync(test: true, IssueRefreshToken());

        result.IsSuccess.Should().BeTrue(result.Error);
        var claims = Claims(result.Value.AccessToken);
        claims["tenant_id"].Should().Be(_twin.ToString());
        claims[TestModeClaims.TeamTenantId].Should().Be(_live.ToString());
        _seeded.Should().Be(1);

        await using var db = _factory.CreateContext();
        (await db.RefreshTokens.SingleAsync(r => r.Token == result.Value.RefreshToken)).DataTenantId.Should().Be(_twin);

        // Once the registry knows it, the next switch neither creates nor seeds again.
        _store.GetAsync(_live, Arg.Any<CancellationToken>()).Returns(Live(_twin));
        (await SwitchAsync(test: true, result.Value.RefreshToken)).IsSuccess.Should().BeTrue();
        _registry.Calls.Should().Be(1);
        _seeded.Should().Be(1);
    }

    [Fact]
    public async Task Leaving_needs_no_permission_and_gives_back_an_ordinary_token()
    {
        var entered = await SwitchAsync(test: true, IssueRefreshToken());

        var left = await SwitchAsync(test: false, entered.Value.RefreshToken, permissions: []);

        left.IsSuccess.Should().BeTrue(left.Error);
        var claims = Claims(left.Value.AccessToken);
        claims["tenant_id"].Should().Be(_live.ToString());
        claims.Should().NotContainKey(TestModeClaims.TeamTenantId);

        await using var db = _factory.CreateContext();
        (await db.RefreshTokens.SingleAsync(r => r.Token == entered.Value.RefreshToken)).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Entering_without_the_permission_or_while_impersonating_is_refused()
    {
        (await SwitchAsync(test: true, IssueRefreshToken(), permissions: [])).Error.Should().Be(TestModeErrors.Forbidden);
        (await SwitchAsync(test: true, IssueRefreshToken(), impersonator: Guid.NewGuid())).Error.Should().Be(TestModeErrors.Impersonating);
        _registry.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Another_users_refresh_token_does_not_switch_anything()
    {
        string stranger;
        await using (var db = _factory.CreateContext())
        {
            var token = DomainRefreshToken.Create(_live, Guid.NewGuid(), TimeSpan.FromDays(1));
            db.RefreshTokens.Add(token);
            await db.SaveChangesAsync();
            stranger = token.Token;
        }

        (await SwitchAsync(test: true, stranger)).Error.Should().Be(TestModeErrors.SessionInvalid);
    }

    [Fact]
    public async Task A_refresh_keeps_the_mode_while_the_twin_is_still_this_tenants()
    {
        var entered = await SwitchAsync(test: true, IssueRefreshToken());

        var refreshed = await RefreshAsync(entered.Value.RefreshToken);
        Claims(refreshed.Value.AccessToken)["tenant_id"].Should().Be(_twin.ToString());

        // A twin the registry no longer ties to this tenant: back to production, not an error.
        _store.GetAsync(_twin, Arg.Any<CancellationToken>()).Returns((TenantInfo?)null);
        var dropped = await RefreshAsync(refreshed.Value.RefreshToken);
        Claims(dropped.Value.AccessToken)["tenant_id"].Should().Be(_live.ToString());
    }

    [Fact]
    public async Task In_test_mode_the_team_is_productions_and_the_products_are_the_sandboxs()
    {
        TenantTwins.Remember(new TenantInfo(_twin, "Caisse (test)", "caisse.ci~test", true, false, null, null, IsTest: true, ParentTenantId: _live));
        await using (var db = _factory.CreateContext())
        {
            db.ProductSpecialities.Add(ProductSpeciality.Create(_live, "Crédit campagne", "CC", ProductCategory.Loan));
            await db.SaveChangesAsync();
        }

        await using var sandbox = new Sankore.Modules.Administration.Infrastructure.AdministrationDbContext(
            new DbContextOptionsBuilder<Sankore.Modules.Administration.Infrastructure.AdministrationDbContext>()
                .UseInMemoryDatabase(DatabaseNameOf(_factory)).Options,
            new FixedTenantContext(_twin));

        (await sandbox.Users.Select(u => u.Id).ToListAsync()).Should().Equal(_user.Id);
        (await sandbox.ProductSpecialities.AnyAsync()).Should().BeFalse("production's catalogue is not the sandbox's");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string IssueRefreshToken()
    {
        using var db = _factory.CreateContext();
        var token = DomainRefreshToken.Create(_live, _user.Id, TimeSpan.FromDays(1));
        db.RefreshTokens.Add(token);
        db.SaveChanges();
        return token.Token;
    }

    private async Task<Result<Sankore.Modules.Administration.Features.Authentication.Login.LoginResult>> SwitchAsync(
        bool test, string refreshToken, IReadOnlyList<string>? permissions = null, Guid? impersonator = null)
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(_user.Id);
        me.TenantId.Returns(_live);
        me.TeamTenantId.Returns(_live);
        me.Permissions.Returns(permissions ?? [Permissions.CanUseTestMode.Code]);
        me.ImpersonatorId.Returns(impersonator);

        var seeders = new ServiceCollection()
            .AddSingleton<ITenantSeeder>(new TenantSeeder("Test", (_, _) => { _seeded++; return Task.CompletedTask; }))
            .BuildServiceProvider();

        await using var db = _factory.CreateContext();
        return await new SwitchTestModeHandler(
                db, UserManager(), me, _store, Substitute.For<ITenantCacheInvalidator>(), _registry,
                new TenantProvisioner(seeders.GetServices<ITenantSeeder>(), seeders.GetRequiredService<IServiceScopeFactory>(),
                    NullLogger<TenantProvisioner>.Instance),
                new JwtTokenService(Options.Create(Jwt)), Options.Create(Jwt), NullLogger<SwitchTestModeHandler>.Instance)
            .Handle(new SwitchTestModeCommand(test, refreshToken), CancellationToken.None);
    }

    private async Task<Result<Sankore.Modules.Administration.Features.Authentication.Login.LoginResult>> RefreshAsync(string token)
    {
        await using var db = _factory.CreateContext();
        return await new RefreshTokenHandler(db, UserManager(), _store, new JwtTokenService(Options.Create(Jwt)), Options.Create(Jwt))
            .Handle(new RefreshTokenCommand(token), CancellationToken.None);
    }

    private static Microsoft.AspNetCore.Identity.UserManager<AppUser> UserManager()
    {
        var manager = IdentityMockFactory.BuildUserManager();
        manager.GetRolesAsync(Arg.Any<AppUser>()).Returns(new List<string>());
        return manager;
    }

    private sealed class FakeTwinRegistry : ITestTwinRegistry
    {
        public Guid Twin { get; set; }
        public int Calls { get; private set; }

        public Task<RegistryResult<Guid>> EnsureTwinAsync(Guid tenantId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new RegistryResult<Guid>(Twin, null));
        }
    }

    private static Dictionary<string, string> Claims(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
            .GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value);

    private static string DatabaseNameOf(TestAdminDbContextFactory factory)
        => (string)typeof(TestAdminDbContextFactory)
            .GetField("_databaseName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(factory)!;
}
