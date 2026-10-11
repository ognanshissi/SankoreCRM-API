namespace Sankore.Modules.Customers.Tests.Features.Compliance;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Compliance.ProfileNotices;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Modules.Customers.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel.Authorization;
using Xunit;

/// <summary>
/// The CIMA LBC/FT fifteen-day obligation: « toute information modifiant le profil du client doit
/// être intégrée au système d'information dans un délai maximum de 15 jours ».
///
/// <para>
/// It was unmeasurable, not merely unenforced. <c>Client.UpdatedAt</c> records when the file was
/// changed, which is the END of the fifteen days; nothing recorded the beginning — an agent being
/// told on the telephone that a client has moved. An institution could be in breach for a month
/// and have no way to know.
/// </para>
/// </summary>
public sealed class ProfileChangeNoticeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 11);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _agencyId = Guid.NewGuid();
    private readonly TestCustomersDbContextFactory _factory;

    public ProfileChangeNoticeTests() => _factory = new TestCustomersDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.TenantId.Returns(_tenant);
        user.Id.Returns(_actor);
        return user;
    }

    private IAgencyScopeProvider Unrestricted()
    {
        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return scope;
    }

    private ICustomerSettings SettingsAllowing(int days)
    {
        var settings = Substitute.For<ICustomerSettings>();
        settings.GetIntAsync(
                Arg.Any<Guid>(), CustomerSettingKeys.ProfileChangeIntegrationDays,
                Arg.Any<CancellationToken>())
            .Returns(days);
        return settings;
    }

    private async Task<Client> SeedClientAsync()
    {
        await using var db = _factory.CreateContext();
        return await TestClientFactory.SeedAsync(
            db, TestClientFactory.Individual(_tenant, _agencyId));
    }

    private async Task<Guid> RecordAsync(
        Guid clientId,
        DateOnly? noticedOn = null,
        ProfileChangeKind kind = ProfileChangeKind.Address)
    {
        await using var db = _factory.CreateContext();

        var result = await new RecordProfileNoticeHandler(
                db, User(), Unrestricted(), new FixedTimeProvider(Now))
            .Handle(
                new RecordProfileNoticeCommand(clientId, kind, noticedOn ?? Today, "Appel du client"),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value;
    }

    private async Task<Sankore.Shared.Kernel.PagedResult<OverdueProfileNoticeDto>> OverdueAsync(
        int allowedDays = 15)
    {
        await using var db = _factory.CreateContext();

        var result = await new OverdueProfileNoticesHandler(
                db, SettingsAllowing(allowedDays), User(), new FixedTimeProvider(Now))
            .Handle(new OverdueProfileNoticesQuery(1, 50), CancellationToken.None);

        return result.Value!;
    }

    private async Task<ClientProfileChangeNotice> ReloadAsync(Guid noticeId)
    {
        await using var db = _factory.CreateContext();
        return await db.ClientProfileChangeNotices.FirstAsync(n => n.Id == noticeId);
    }

    // ── recording ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_notice_records_when_the_INSTITUTION_learned_of_the_change()
    {
        // Not when the change happened, and not when the file was updated. It is the start of the
        // fifteen days, and it is caller-supplied because an agent records on Monday what a
        // client told them on Friday.
        var client = await SeedClientAsync();
        var friday = Today.AddDays(-3);

        var noticeId = await RecordAsync(client.Id, noticedOn: friday);

        var notice = await ReloadAsync(noticeId);
        notice.NoticedOn.Should().Be(friday);
        notice.IsOpen.Should().BeTrue();
        notice.ElapsedDays(Today).Should().Be(3);
    }

    [Fact]
    public async Task The_recorder_is_taken_from_the_token_and_never_from_the_body()
    {
        // The rule this solution applies to every audit actor: a caller-supplied id lets an agent
        // attribute their own entry to a colleague.
        var client = await SeedClientAsync();

        var notice = await ReloadAsync(await RecordAsync(client.Id));

        notice.RecordedBy.Should().Be(_actor);
        typeof(RecordProfileNoticeRequest).GetProperties().Select(p => p.Name)
            .Should().NotContain("RecordedBy");
    }

    [Fact]
    public void A_change_noticed_in_the_future_is_refused()
    {
        var act = () => ClientProfileChangeNotice.Record(
            _tenant, Guid.NewGuid(), ProfileChangeKind.Phone,
            noticedOn: Today.AddDays(1), recordedBy: _actor, now: Now);

        act.Should().Throw<Sankore.Shared.Kernel.DomainException>();
    }

    [Fact]
    public async Task A_client_outside_the_perimeter_reads_as_NOT_FOUND()
    {
        var client = await SeedClientAsync();

        var scope = Substitute.For<IAgencyScopeProvider>();
        scope.CanAccessAgencyAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        await using var db = _factory.CreateContext();
        var result = await new RecordProfileNoticeHandler(
                db, User(), scope, new FixedTimeProvider(Now))
            .Handle(
                new RecordProfileNoticeCommand(
                    client.Id, ProfileChangeKind.Address, Today, null),
                CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ClientNotFound);
    }

    // ── the deadline ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_notice_within_the_allowance_is_not_overdue()
    {
        var client = await SeedClientAsync();
        await RecordAsync(client.Id, noticedOn: Today.AddDays(-10));

        (await OverdueAsync(allowedDays: 15)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_notice_past_the_allowance_is_reported_with_its_elapsed_days()
    {
        var client = await SeedClientAsync();
        await RecordAsync(client.Id, noticedOn: Today.AddDays(-40));

        var row = (await OverdueAsync(allowedDays: 15)).Items.Should().ContainSingle().Subject;

        row.ClientId.Should().Be(client.Id);
        row.ElapsedDays.Should().Be(40);
        row.AllowedDays.Should().Be(15, "a screen must not hard-code fifteen");
        row.Kind.Should().Be(nameof(ProfileChangeKind.Address));
    }

    [Fact]
    public async Task The_allowance_is_the_tenants_to_set()
    {
        var client = await SeedClientAsync();
        await RecordAsync(client.Id, noticedOn: Today.AddDays(-20));

        (await OverdueAsync(allowedDays: 15)).Items.Should().HaveCount(1);
        (await OverdueAsync(allowedDays: 30)).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_report_and_the_aggregate_agree_about_what_overdue_means()
    {
        // The SQL cutoff in the handler and IsOverdue on the aggregate are the same comparison
        // written twice — in different languages, which is the one case this repository tolerates.
        // This pins them together at the boundary, where an off-by-one would live.
        var client = await SeedClientAsync();

        var exactlyAtTheLimit = await RecordAsync(client.Id, noticedOn: Today.AddDays(-15));
        var oneDayPast = await RecordAsync(client.Id, noticedOn: Today.AddDays(-16));

        var atLimit = await ReloadAsync(exactlyAtTheLimit);
        var past = await ReloadAsync(oneDayPast);

        atLimit.IsOverdue(Today, allowedDays: 15).Should().BeFalse();
        past.IsOverdue(Today, allowedDays: 15).Should().BeTrue();

        (await OverdueAsync(allowedDays: 15)).Items
            .Select(i => i.NoticeId).Should().Equal(oneDayPast);
    }

    [Fact]
    public async Task The_oldest_breach_is_listed_first()
    {
        var client = await SeedClientAsync();
        var recent = await RecordAsync(client.Id, noticedOn: Today.AddDays(-20));
        var ancient = await RecordAsync(client.Id, noticedOn: Today.AddDays(-200));

        (await OverdueAsync()).Items.Select(i => i.NoticeId).Should().Equal(ancient, recent);
    }

    // ── closing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Integrating_stops_the_clock_and_takes_it_out_of_the_report()
    {
        var client = await SeedClientAsync();
        var noticeId = await RecordAsync(client.Id, noticedOn: Today.AddDays(-40));

        await using (var db = _factory.CreateContext())
        {
            var closed = await new IntegrateProfileNoticeHandler(
                    db, User(), new FixedTimeProvider(Now))
                .Handle(new IntegrateProfileNoticeCommand(noticeId), CancellationToken.None);

            closed.IsSuccess.Should().BeTrue(closed.Error);
        }

        var notice = await ReloadAsync(noticeId);
        notice.IsOpen.Should().BeFalse();
        notice.IntegratedOn.Should().Be(Today);
        notice.IntegratedBy.Should().Be(_actor);

        // And the elapsed days freeze at the integration rather than going on accruing — which is
        // the figure a supervisor asks for: how long did it TAKE.
        notice.ElapsedDays(Today.AddDays(100)).Should().Be(40);

        (await OverdueAsync()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_twice_is_refused()
    {
        var client = await SeedClientAsync();
        var noticeId = await RecordAsync(client.Id);

        await using (var db = _factory.CreateContext())
        {
            await new IntegrateProfileNoticeHandler(db, User(), new FixedTimeProvider(Now))
                .Handle(new IntegrateProfileNoticeCommand(noticeId), CancellationToken.None);
        }

        await using var second = _factory.CreateContext();
        var again = await new IntegrateProfileNoticeHandler(
                second, User(), new FixedTimeProvider(Now))
            .Handle(new IntegrateProfileNoticeCommand(noticeId), CancellationToken.None);

        again.IsFailure.Should().BeTrue();
        again.Error.Should().Be(CustomerErrors.ProfileNoticeAlreadyClosed);
    }

    [Fact]
    public async Task A_dismissed_notice_is_never_overdue_and_keeps_its_reason()
    {
        // A notice that simply disappeared would be indistinguishable from one somebody forgot,
        // which is why the reason is required.
        var client = await SeedClientAsync();
        var noticeId = await RecordAsync(client.Id, noticedOn: Today.AddDays(-90));

        await using (var db = _factory.CreateContext())
        {
            var dismissed = await new DismissProfileNoticeHandler(
                    db, User(), new FixedTimeProvider(Now))
                .Handle(
                    new DismissProfileNoticeCommand(noticeId, "Le client s'était trompé de dossier"),
                    CancellationToken.None);

            dismissed.IsSuccess.Should().BeTrue(dismissed.Error);
        }

        var notice = await ReloadAsync(noticeId);
        notice.IsOpen.Should().BeFalse();
        notice.DismissedReason.Should().Contain("trompé");
        notice.IsOverdue(Today, allowedDays: 15).Should().BeFalse();

        (await OverdueAsync()).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Dismissing_with_no_reason_is_refused()
    {
        var client = await SeedClientAsync();
        var noticeId = await RecordAsync(client.Id);

        await using var db = _factory.CreateContext();
        var result = await new DismissProfileNoticeHandler(db, User(), new FixedTimeProvider(Now))
            .Handle(new DismissProfileNoticeCommand(noticeId, "   "), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ProfileNoticeDismissReasonRequired);
    }

    [Fact]
    public void An_integration_dated_before_the_notice_is_refused()
    {
        // It would show as a NEGATIVE delay in the compliance report — a figure nobody can act on.
        var notice = ClientProfileChangeNotice.Record(
            _tenant, Guid.NewGuid(), ProfileChangeKind.Phone,
            noticedOn: Today, recordedBy: _actor, now: Now);

        var result = notice.Integrate(_actor, Today.AddDays(-1));

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(CustomerErrors.ProfileNoticeIntegratedBeforeNoticed);
    }

    [Fact]
    public async Task Another_tenants_overdue_notice_is_not_listed()
    {
        var client = await SeedClientAsync();
        await RecordAsync(client.Id, noticedOn: Today.AddDays(-40));

        await using var other = _factory.ContextFor(Guid.NewGuid());
        var otherResult = await new OverdueProfileNoticesHandler(
                other, SettingsAllowing(15), User(), new FixedTimeProvider(Now))
            .Handle(new OverdueProfileNoticesQuery(1, 50), CancellationToken.None);

        // The handler reads the CALLER's tenant from ICurrentUser while the context is bound to
        // another: the query filter is what makes the two disagree safely.
        otherResult.Value!.Items.Should().BeEmpty();
    }
}
