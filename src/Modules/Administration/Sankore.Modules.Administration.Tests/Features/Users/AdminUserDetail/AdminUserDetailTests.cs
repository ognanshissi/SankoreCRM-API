namespace Sankore.Modules.Administration.Tests.Features.Users.AdminUserDetail;

using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Impersonation;
using Sankore.Modules.Administration.Features.Users.Invitation;
using Sankore.Modules.Administration.Features.Users.UpdateUser;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Identity;
using Sankore.Modules.Administration.Infrastructure.JwtToken;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class AdminUserDetailTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private readonly UserManager<AppUser> _userManager = IdentityMockFactory.BuildUserManager();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();
    private readonly List<QueueEmailRequest> _emails = [];

    public AdminUserDetailTests()
    {
        _factory = new TestAdminDbContextFactory(_tenantId);
        _userManager.GenerateUserTokenAsync(Arg.Any<AppUser>(), ActivationTokens.ProviderName, ActivationTokens.Purpose)
            .Returns("activation-token");
        _userManager.GetRolesAsync(Arg.Any<AppUser>()).Returns(new List<string> { "CommercialAgent" });
        _notifications.QueueEmailAsync(Arg.Any<QueueEmailRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _emails.Add(call.Arg<QueueEmailRequest>());
                return Result<Guid>.Ok(Guid.NewGuid());
            });
    }

    public void Dispose() => _factory.Dispose();

    private async Task<AppUser> SeedAsync(string email, bool active, Action<AppUser>? tweak = null)
    {
        await using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, _agencyId, "Oumou", "Sidibe", email);
        // UserManager.CreateAsync normalise l'adresse en production ; l'amorçage le fait à sa place.
        user.NormalizedEmail = email.ToUpperInvariant();
        if (active) user.Activate();
        tweak?.Invoke(user);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private ActivationInvitationSender Invitations(AdministrationDbContext db) => new(
        _userManager, Substitute.For<ITenantStore>(), _notifications,
        Options.Create(new IdentityTokenOptions { ActivationTokenLifespan = TimeSpan.FromHours(72) }), TimeProvider.System);

    private static ICurrentUser Caller(Guid id, Guid tenantId, Guid? impersonator = null)
    {
        var current = Substitute.For<ICurrentUser>();
        current.Id.Returns(id);
        current.TenantId.Returns(tenantId);
        current.ImpersonatorId.Returns(impersonator);
        return current;
    }

    // ── Invitation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Resending_an_invitation_rotates_the_previous_links_and_dates_the_new_one()
    {
        var user = await SeedAsync("oumou@imf.ci", active: false);

        await using var db = _factory.CreateContext();
        var result = await new ResendInvitationHandler(db, Invitations(db))
            .Handle(new ResendInvitationCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        (result.Value.ExpiresAt - result.Value.SentAt).Should().Be(TimeSpan.FromHours(72));
        await _userManager.Received(1).UpdateSecurityStampAsync(Arg.Is<AppUser>(u => u.Id == user.Id));
        _emails.Should().ContainSingle().Which.RecipientEmail.Should().Be("oumou@imf.ci");

        await using var verify = _factory.CreateContext();
        (await verify.Users.SingleAsync(u => u.Id == user.Id)).InvitationSentAt.Should().Be(result.Value.SentAt);
    }

    [Fact]
    public async Task An_active_account_has_no_invitation_to_resend()
    {
        var user = await SeedAsync("oumou@imf.ci", active: true);

        await using var db = _factory.CreateContext();
        var result = await new ResendInvitationHandler(db, Invitations(db))
            .Handle(new ResendInvitationCommand(user.Id), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _emails.Should().BeEmpty();
    }

    // ── Adresse ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Correcting_the_address_of_a_pending_account_re_invites_the_new_address()
    {
        var user = await SeedAsync("oumou@imf.co", active: false);

        await using var db = _factory.CreateContext();
        var result = await new UpdateUserHandler(db, Invitations(db)).Handle(
            new UpdateUserCommand(user.Id, null, null, null, null, null, Email: "oumou@imf.ci", WorkPhone: "+225 07 00 00 00 00"),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _emails.Should().ContainSingle().Which.RecipientEmail.Should().Be("oumou@imf.ci");
        await using var verify = _factory.CreateContext();
        var saved = await verify.Users.SingleAsync(u => u.Id == user.Id);
        saved.Email.Should().Be("oumou@imf.ci");
        saved.NormalizedUserName.Should().Be("OUMOU@IMF.CI");
        (await verify.UserProfiles.SingleAsync(p => p.UserId == user.Id)).WorkNumber.Contact.Should().Be("+225 07 00 00 00 00");
    }

    [Fact]
    public async Task The_address_of_an_activated_account_is_locked()
    {
        var user = await SeedAsync("oumou@imf.ci", active: true);

        await using var db = _factory.CreateContext();
        var result = await new UpdateUserHandler(db, Invitations(db)).Handle(
            new UpdateUserCommand(user.Id, null, null, null, null, null, Email: "autre@imf.ci"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        _emails.Should().BeEmpty();
    }

    [Fact]
    public async Task An_address_already_used_in_the_tenant_is_refused()
    {
        await SeedAsync("pris@imf.ci", active: true);
        var user = await SeedAsync("oumou@imf.ci", active: false);

        await using var db = _factory.CreateContext();
        var result = await new UpdateUserHandler(db, Invitations(db)).Handle(
            new UpdateUserCommand(user.Id, null, null, null, null, null, Email: "PRIS@imf.ci"), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
    }

    // ── Matricule ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Employee_numbers_follow_the_highest_one_given()
    {
        await SeedAsync("a@imf.ci", active: true, u => u.SetEmployeeNumber("AGT-0216"));
        await SeedAsync("b@imf.ci", active: true, u => u.SetEmployeeNumber("AGT-0009"));

        await using var db = _factory.CreateContext();
        (await new EmployeeNumberAllocator(db).NextAsync(_tenantId, CancellationToken.None)).Should().Be("AGT-0217");
    }

    // ── Usurpation ─────────────────────────────────────────────────────────

    private ImpersonateUserHandler Impersonation(AdministrationDbContext db, ICurrentUser caller) => new(
        db, _userManager, caller,
        new JwtTokenService(Options.Create(new JwtOptions
        {
            SigningKey = "test-signing-key-at-least-thirty-two-bytes-long!!",
            Issuer = "sankore", Audience = "sankore",
        })));

    [Fact]
    public async Task Impersonation_opens_a_short_revocable_session_carrying_the_administrator()
    {
        var admin = await SeedAsync("admin@imf.ci", active: true);
        var agent = await SeedAsync("oumou@imf.ci", active: true);

        await using var db = _factory.CreateContext();
        var result = await Impersonation(db, Caller(admin.Id, _tenantId))
            .Handle(new ImpersonateUserCommand(agent.Id, "Vérifier son portefeuille", null, null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(result.Value.AccessToken);
        jwt.Subject.Should().Be(agent.Id.ToString());
        jwt.Claims.Single(c => c.Type == ImpersonationClaims.ActorId).Value.Should().Be(admin.Id.ToString());
        (result.Value.ExpiresAt - DateTimeOffset.UtcNow).Should().BeLessThanOrEqualTo(ImpersonateUserHandler.Lifetime);

        var sid = Guid.Parse(jwt.Claims.Single(c => c.Type == SessionClaim.Type).Value);
        await using var verify = _factory.CreateContext();
        var session = await verify.UserSessions.SingleAsync(s => s.Id == sid);
        session.UserId.Should().Be(agent.Id);
        session.ImpersonatorUserId.Should().Be(admin.Id);
        (await verify.RefreshTokens.AnyAsync(t => t.SessionId == sid)).Should().BeFalse("une usurpation ne se prolonge pas");
    }

    [Fact]
    public async Task Ending_the_impersonation_closes_its_session()
    {
        var admin = await SeedAsync("admin@imf.ci", active: true);
        var agent = await SeedAsync("oumou@imf.ci", active: true);
        await using var db = _factory.CreateContext();
        var started = await Impersonation(db, Caller(admin.Id, _tenantId))
            .Handle(new ImpersonateUserCommand(agent.Id, "Vérifier son portefeuille", null, null), CancellationToken.None);
        var sid = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(started.Value.AccessToken)
            .Claims.Single(c => c.Type == SessionClaim.Type).Value);

        await using var endDb = _factory.CreateContext();
        var ended = await new EndImpersonationHandler(endDb, Caller(agent.Id, _tenantId, impersonator: admin.Id))
            .Handle(new EndImpersonationCommand(sid), CancellationToken.None);

        ended.IsSuccess.Should().BeTrue();
        await using var verify = _factory.CreateContext();
        (await verify.UserSessions.SingleAsync(s => s.Id == sid)).EndReason.Should().Be(SessionEndReason.ImpersonationEnded);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("inactive")]
    [InlineData("superuser")]
    [InlineData("chained")]
    public async Task Impersonation_is_refused_where_it_would_escape_its_purpose(string scenario)
    {
        var admin = await SeedAsync("admin@imf.ci", active: true);
        var target = scenario switch
        {
            "self" => admin,
            "inactive" => await SeedAsync("pending@imf.ci", active: false),
            "superuser" => await SeedRootAsync(),
            _ => await SeedAsync("oumou@imf.ci", active: true),
        };
        var caller = Caller(admin.Id, _tenantId, impersonator: scenario == "chained" ? Guid.NewGuid() : null);

        await using var db = _factory.CreateContext();
        var result = await Impersonation(db, caller)
            .Handle(new ImpersonateUserCommand(target.Id, "Vérifier son portefeuille", null, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await using var verify = _factory.CreateContext();
        (await verify.UserSessions.AnyAsync()).Should().BeFalse();
    }

    private async Task<AppUser> SeedRootAsync()
    {
        await using var db = _factory.CreateContext();
        var root = AppUser.CreateRoot(_tenantId, "Root", "Admin", "root@imf.ci");
        db.Users.Add(root);
        await db.SaveChangesAsync();
        return root;
    }
}
