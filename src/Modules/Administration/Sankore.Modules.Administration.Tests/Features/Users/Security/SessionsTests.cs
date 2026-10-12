namespace Sankore.Modules.Administration.Tests.Features.Users.Security;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Features.Users.Security;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Administration.Infrastructure.Sessions;
using Sankore.Modules.Administration.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Xunit;

public sealed class SessionsTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly TestAdminDbContextFactory _factory;
    private Guid _userId;

    public SessionsTests() => _factory = new TestAdminDbContextFactory(_tenantId);

    public void Dispose() => _factory.Dispose();

    /// <summary>Un utilisateur et trois sessions ouvertes, chacune avec son jeton de rafraîchissement.</summary>
    private async Task<Guid[]> SeedAsync()
    {
        await using var db = _factory.CreateContext();
        var user = AppUser.Create(_tenantId, Guid.NewGuid(), "Ibrahim", "Sanogo", "i.sanogo@imf.ci");
        db.Users.Add(user);
        _userId = user.Id;

        var ids = new List<Guid>();
        foreach (var browser in new[] { "Chrome", "Firefox", "Safari" })
        {
            var session = UserSession.Open(_tenantId, user.Id, "10.0.0.1",
                new UserAgentInfo(null, browser, null, LoginPlatform.Windows, LoginClientKind.Web));
            db.UserSessions.Add(session);
            db.RefreshTokens.Add(RefreshToken.Create(_tenantId, user.Id, TimeSpan.FromDays(7), session.Id));
            ids.Add(session.Id);
        }
        await db.SaveChangesAsync();
        return ids.ToArray();
    }

    private ICurrentUser Me()
    {
        var current = Substitute.For<ICurrentUser>();
        current.Id.Returns(_userId);
        current.TenantId.Returns(_tenantId);
        return current;
    }

    private async Task<List<UserSession>> SessionsAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.UserSessions.Where(s => s.UserId == _userId).ToListAsync();
    }

    [Fact]
    public async Task Lists_open_sessions_with_the_current_one_first()
    {
        var ids = await SeedAsync();

        var result = await new ListMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new ListMySessionsQuery(ids[2]), CancellationToken.None);

        result.Value.Should().HaveCount(3);
        result.Value[0].Id.Should().Be(ids[2]);
        result.Value[0].IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task Ending_another_session_revokes_it_and_its_refresh_token_only()
    {
        var ids = await SeedAsync();

        var result = await new EndMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new EndMySessionsCommand(ids[0], ids[1]), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var sessions = await SessionsAsync();
        sessions.Single(s => s.Id == ids[1]).EndReason.Should().Be(SessionEndReason.RevokedByUser);
        sessions.Where(s => s.Id != ids[1]).Should().OnlyContain(s => s.RevokedAt == null);

        await using var db = _factory.CreateContext();
        (await db.RefreshTokens.SingleAsync(t => t.SessionId == ids[1])).RevokedAt.Should().NotBeNull();
        (await db.RefreshTokens.SingleAsync(t => t.SessionId == ids[0])).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_current_session_cannot_be_ended_from_the_list()
    {
        var ids = await SeedAsync();

        var result = await new EndMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new EndMySessionsCommand(ids[0], ids[0]), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await SessionsAsync()).Should().OnlyContain(s => s.RevokedAt == null);
    }

    [Fact]
    public async Task Ending_the_others_keeps_the_current_session()
    {
        var ids = await SeedAsync();

        var result = await new EndMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new EndMySessionsCommand(ids[0], null), CancellationToken.None);

        result.Value.Should().Be(2);
        var sessions = await SessionsAsync();
        sessions.Single(s => s.Id == ids[0]).RevokedAt.Should().BeNull();
        sessions.Where(s => s.Id != ids[0]).Should().OnlyContain(s => s.RevokedAt != null);
    }

    [Fact]
    public async Task Without_a_known_current_session_ending_the_others_is_refused()
    {
        await SeedAsync();

        var result = await new EndMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new EndMySessionsCommand(null, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await SessionsAsync()).Should().OnlyContain(s => s.RevokedAt == null);
    }

    [Fact]
    public async Task A_revoked_session_is_refused_by_the_validator()
    {
        var ids = await SeedAsync();
        var validator = new SessionValidator(_factory.CreateContext());
        (await validator.IsActiveAsync(_tenantId, ids[1], CancellationToken.None)).Should().BeTrue();

        await new EndMySessionsHandler(_factory.CreateContext(), Me())
            .Handle(new EndMySessionsCommand(ids[0], ids[1]), CancellationToken.None);

        // Même instance, même cache : la fermeture l'a vidé, le refus est immédiat.
        (await new SessionValidator(_factory.CreateContext()).IsActiveAsync(_tenantId, ids[1], CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_session_of_another_tenant_is_refused()
    {
        var ids = await SeedAsync();

        (await new SessionValidator(_factory.CreateContext()).IsActiveAsync(Guid.NewGuid(), ids[0], CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Reporting_a_login_marks_it_and_ends_every_other_session()
    {
        var ids = await SeedAsync();
        Guid loginId;
        await using (var db = _factory.CreateContext())
        {
            var login = UserLoginLocation.Create(_tenantId, _userId, null, "102.89.1.1",
                new UserAgentInfo(null, null, null, LoginPlatform.Unknown, LoginClientKind.Unknown),
                LoginOutcome.InvalidPassword);
            db.UserLoginLocations.Add(login);
            await db.SaveChangesAsync();
            loginId = login.Id;
        }

        // Sans superviseur, le notifieur s'arrête avant d'envoyer quoi que ce soit.
        var context = _factory.CreateContext();
        var notifier = new SuspiciousLoginNotifier(context, null!, NullLogger<SuspiciousLoginNotifier>.Instance);
        var result = await new ReportSuspiciousLoginHandler(context, Me(), notifier)
            .Handle(new ReportSuspiciousLoginCommand(ids[0], loginId, "Je n'étais pas à Lagos"), CancellationToken.None);

        result.Value.Should().Be(2);
        var sessions = await SessionsAsync();
        sessions.Single(s => s.Id == ids[0]).RevokedAt.Should().BeNull();
        sessions.Where(s => s.Id != ids[0]).Should()
            .OnlyContain(s => s.EndReason == SessionEndReason.SuspiciousLoginReported);

        await using var verify = _factory.CreateContext();
        (await verify.UserLoginLocations.SingleAsync(l => l.Id == loginId)).ReportedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_login_of_someone_else_cannot_be_reported()
    {
        var ids = await SeedAsync();
        Guid foreignLogin;
        await using (var db = _factory.CreateContext())
        {
            var login = UserLoginLocation.Create(_tenantId, Guid.NewGuid(), null, null, null);
            db.UserLoginLocations.Add(login);
            await db.SaveChangesAsync();
            foreignLogin = login.Id;
        }

        var context = _factory.CreateContext();
        var notifier = new SuspiciousLoginNotifier(context, null!, NullLogger<SuspiciousLoginNotifier>.Instance);
        var result = await new ReportSuspiciousLoginHandler(context, Me(), notifier)
            .Handle(new ReportSuspiciousLoginCommand(ids[0], foreignLogin, null), CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        (await SessionsAsync()).Should().OnlyContain(s => s.RevokedAt == null);
    }
}
