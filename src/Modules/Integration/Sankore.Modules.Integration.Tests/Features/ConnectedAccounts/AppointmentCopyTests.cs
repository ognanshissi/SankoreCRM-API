namespace Sankore.Modules.Integration.Tests.Features.ConnectedAccounts;

using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.ConnectedAccounts;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.Features.Telephony;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected agendas, lot 4: SANKORÉ rendez-vous copied into each participant's target agenda.</summary>
public sealed class AppointmentCopyTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _organizer = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _noTarget = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ILeadsModule _leads = Substitute.For<ILeadsModule>();
    private readonly Secrets _secrets = new();
    private readonly WritingProvider _provider = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<Guid, AppointmentCalendarEntry> _appointments = [];

    public AppointmentCopyTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _leads.GetAppointmentForCalendarAsync(_tenant, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => _appointments.GetValueOrDefault(ci.ArgAt<Guid>(1)));
        _leads.ListAppointmentsForUserAsync(_tenant, Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci => _appointments.Values
                .Where(a => !a.IsCancelled && (a.OrganizerId == ci.ArgAt<Guid>(1) || a.ParticipantIds.Contains(ci.ArgAt<Guid>(1))))
                .ToList());
    }

    public void Dispose() => _factory.Dispose();

    // ── Doubles ────────────────────────────────────────────────────────────────

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class Secrets : ISecretsModule
    {
        private readonly Dictionary<SecretKey, (string Value, DateTimeOffset? ExpiresAt)> _entries = [];

        public Task SetAsync(SecretKey key, string value, DateTimeOffset? expiresAt = null, CancellationToken ct = default)
        {
            _entries[key] = (value, expiresAt);
            return Task.CompletedTask;
        }

        public Task<SecretHint?> GetHintAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var e) ? new SecretHint(key.Name, "***", e.ExpiresAt) : null);

        public Task<string?> GetValueAsync(SecretKey key, CancellationToken ct = default) =>
            Task.FromResult(_entries.TryGetValue(key, out var e) ? e.Value : null);

        public Task DeleteAllAsync(Guid tenantId, string scope, Guid entityId, CancellationToken ct = default)
        {
            foreach (var key in _entries.Keys.Where(k => k.EntityId == entityId).ToList()) _entries.Remove(key);
            return Task.CompletedTask;
        }
    }

    /// <summary>Holds the events written per agenda, like a provider would.</summary>
    private sealed class WritingProvider : IConnectedAccountProvider
    {
        public Dictionary<string, (string Calendar, ProviderEventDraft Draft)> Events { get; } = [];
        public int Writes { get; private set; }
        public bool Fail { get; set; }

        public ConnectedAccountProvider Provider => ConnectedAccountProvider.Google;
        public bool IsAvailable => true;
        public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) => redirectUri;
        public Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
            Task.FromResult(new ProviderTokens("access", null, DateTimeOffset.UtcNow.AddHours(1), ""));
        public Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderEventPage> ReadEventsAsync(
            string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct)
        {
            Writes++;
            if (Fail) throw new ProviderAuthException("event_create_http_503", revoked: false);
            var id = $"evt-{Events.Count + 1}";
            Events[id] = (calendarId, draft);
            return Task.FromResult(id);
        }

        public Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct)
        {
            Writes++;
            if (Fail) throw new ProviderAuthException("event_update_http_503", revoked: false);
            if (!Events.ContainsKey(eventId)) return Task.FromResult(false);
            Events[eventId] = (calendarId, draft);
            return Task.FromResult(true);
        }

        public Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct)
        {
            Writes++;
            if (Fail) throw new ProviderAuthException("event_delete_http_503", revoked: false);
            Events.Remove(eventId);
            return Task.CompletedTask;
        }
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    private AppointmentCalendarCopies Copies(IntegrationDbContext db)
    {
        var providers = new ConnectedAccountProviders([_provider]);
        return new AppointmentCalendarCopies(
            db, _leads, providers,
            new ConnectedAccountTokens(db, _secrets, providers, NullLogger<ConnectedAccountTokens>.Instance),
            _clock, NullLogger<AppointmentCalendarCopies>.Instance);
    }

    /// <summary>An account with two writable agendas and a read-only one; the first is the target unless said otherwise.</summary>
    private async Task<(Guid Main, Guid Other, Guid ReadOnly)> SeedUserAsync(Guid userId, bool withTarget = true)
    {
        await using var db = _factory.CreateContext();
        var account = ConnectedAccount.Connect(_tenant, userId, ConnectedAccountProvider.Google, $"ext-{userId:N}", "awa@example.com", "Awa", "Pro", "", _clock.GetUtcNow());
        db.ConnectedAccounts.Add(account);

        var main = ConnectedCalendar.Discover(_tenant, account.Id, userId, "cal-main", "Calendrier", null, true, false, false);
        var other = ConnectedCalendar.Discover(_tenant, account.Id, userId, "cal-other", "Réunions", null, false, false, false);
        var readOnly = ConnectedCalendar.Discover(_tenant, account.Id, userId, "cal-holidays", "Jours fériés", null, false, true, true);
        if (withTarget) main.MakeAppointmentTarget();
        db.ConnectedCalendars.AddRange(main, other, readOnly);
        await db.SaveChangesAsync();

        await _secrets.SetAsync(ConnectedAccountSecrets.AccessToken(_tenant, account.Id), "access", DateTimeOffset.UtcNow.AddHours(1));
        await _secrets.SetAsync(ConnectedAccountSecrets.RefreshToken(_tenant, account.Id), "refresh");
        return (main.Id, other.Id, readOnly.Id);
    }

    private AppointmentCalendarEntry Appointment(int hour = 14, bool cancelled = false, Guid? id = null, string? meetingUrl = null)
    {
        var start = new DateTimeOffset(2026, 10, 23, hour, 0, 0, TimeSpan.Zero);
        var entry = new AppointmentCalendarEntry(
            id ?? Guid.NewGuid(), "Point dossier", start, start.AddHours(1), cancelled, "Agence Plateau", meetingUrl,
            _organizer, [_colleague, _noTarget]);
        _appointments[entry.Id] = entry;
        return entry;
    }

    private async Task ReconcileAsync(Guid appointmentId)
    {
        await using var db = _factory.CreateContext();
        await Copies(db).ReconcileAppointmentAsync(_tenant, appointmentId, CancellationToken.None);
    }

    private async Task<List<AppointmentCalendarCopy>> CopiesAsync()
    {
        await using var db = _factory.CreateContext();
        return await db.AppointmentCalendarCopies.ToListAsync();
    }

    // ── Copying ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_participant_with_a_target_gets_a_copy_and_the_others_none()
    {
        await SeedUserAsync(_organizer);
        await SeedUserAsync(_colleague);
        await SeedUserAsync(_noTarget, withTarget: false);
        var appointment = Appointment();

        await ReconcileAsync(appointment.Id);

        var copies = await CopiesAsync();
        copies.Select(c => c.UserId).Should().BeEquivalentTo([_organizer, _colleague]);
        copies.Should().OnlyContain(c => c.Status == AppointmentCopyStatus.Synced && c.ExternalEventId != null);
        _provider.Events.Values.Should().OnlyContain(e => e.Calendar == "cal-main" && e.Draft.Title == "Point dossier");
    }

    [Fact]
    public void The_copy_carries_the_time_place_and_link_but_never_who_it_is_about()
    {
        var draft = AppointmentCalendarCopies.Draft(Appointment(meetingUrl: "https://teams.microsoft.com/l/abc"));

        draft.Location.Should().Be("Agence Plateau");
        draft.Body.Should().Contain("https://teams.microsoft.com/l/abc").And.Contain("SANKORÉ");
        draft.EndAt.Should().Be(draft.StartAt.AddHours(1));
    }

    [Fact]
    public async Task A_move_updates_the_same_event_and_an_unchanged_appointment_costs_no_call()
    {
        await SeedUserAsync(_organizer);
        var appointment = Appointment();
        await ReconcileAsync(appointment.Id);
        var eventId = (await CopiesAsync()).Single().ExternalEventId;

        Appointment(hour: 16, id: appointment.Id);
        await ReconcileAsync(appointment.Id);

        _provider.Events.Should().ContainSingle();
        _provider.Events[eventId!].Draft.StartAt.Hour.Should().Be(16);

        var writes = _provider.Writes;
        await ReconcileAsync(appointment.Id);
        _provider.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task A_cancellation_deletes_every_copy()
    {
        await SeedUserAsync(_organizer);
        await SeedUserAsync(_colleague);
        var appointment = Appointment();
        await ReconcileAsync(appointment.Id);

        Appointment(cancelled: true, id: appointment.Id);
        await ReconcileAsync(appointment.Id);

        _provider.Events.Should().BeEmpty();
        (await CopiesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_copy_deleted_by_hand_at_the_provider_is_created_again_on_the_next_change()
    {
        await SeedUserAsync(_organizer);
        var appointment = Appointment();
        await ReconcileAsync(appointment.Id);
        _provider.Events.Clear();

        Appointment(hour: 15, id: appointment.Id);
        await ReconcileAsync(appointment.Id);

        _provider.Events.Should().ContainSingle().Which.Value.Draft.StartAt.Hour.Should().Be(15);
        (await CopiesAsync()).Single().ExternalEventId.Should().Be(_provider.Events.Keys.Single());
    }

    [Fact]
    public async Task A_provider_failure_is_recorded_and_retried_by_the_job()
    {
        await SeedUserAsync(_organizer);
        var appointment = Appointment();

        _provider.Fail = true;
        await ReconcileAsync(appointment.Id);
        var failed = (await CopiesAsync()).Single();
        failed.Status.Should().Be(AppointmentCopyStatus.Failed);
        failed.Attempts.Should().Be(1);
        failed.LastError.Should().Be("event_create_http_503");

        _provider.Fail = false;
        await using (var db = _factory.CreateContext())
            await Copies(db).RetryFailedAsync(_tenant, CancellationToken.None);

        (await CopiesAsync()).Single().Status.Should().Be(AppointmentCopyStatus.Synced);
        _provider.Events.Should().ContainSingle();
    }

    // ── Choosing the target ────────────────────────────────────────────────────

    private async Task<Result> SetTargetAsync(Guid userId, Guid? calendarId)
    {
        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(userId);
        me.TenantId.Returns(_tenant);
        await using var db = _factory.CreateContext();
        return await new SetAppointmentTargetHandler(db, me, Substitute.For<IBackgroundJobClient>())
            .Handle(new SetAppointmentTargetCommand(calendarId), CancellationToken.None);
    }

    [Fact]
    public async Task A_read_only_agenda_cannot_be_the_target_and_choosing_another_clears_the_first()
    {
        var (_, other, readOnly) = await SeedUserAsync(_organizer);

        (await SetTargetAsync(_organizer, readOnly)).Error.Should().Be("CALENDAR_READ_ONLY");
        (await SetTargetAsync(_organizer, other)).IsSuccess.Should().BeTrue();

        await using var db = _factory.CreateContext();
        var targets = await db.ConnectedCalendars.Where(c => c.IsAppointmentTarget).Select(c => c.Id).ToListAsync();
        targets.Should().Equal(other);
    }

    [Fact]
    public async Task Another_target_moves_the_upcoming_copies_and_no_target_removes_them()
    {
        var (_, other, _) = await SeedUserAsync(_organizer);
        var appointment = Appointment();
        await ReconcileAsync(appointment.Id);

        (await SetTargetAsync(_organizer, other)).IsSuccess.Should().BeTrue();
        await using (var db = _factory.CreateContext())
            await Copies(db).ReconcileUserAsync(_tenant, _organizer, CancellationToken.None);

        _provider.Events.Should().ContainSingle().Which.Value.Calendar.Should().Be("cal-other");
        (await CopiesAsync()).Single().CalendarId.Should().Be(other);

        (await SetTargetAsync(_organizer, null)).IsSuccess.Should().BeTrue();
        await using (var db = _factory.CreateContext())
            await Copies(db).ReconcileUserAsync(_tenant, _organizer, CancellationToken.None);

        _provider.Events.Should().BeEmpty();
        (await CopiesAsync()).Should().BeEmpty();
    }

    // ── The calendar screen ────────────────────────────────────────────────────

    [Fact]
    public async Task A_copy_read_back_from_the_agenda_is_not_shown_twice_on_the_calendar()
    {
        var (main, _, _) = await SeedUserAsync(_organizer);
        var appointment = Appointment();
        await ReconcileAsync(appointment.Id);
        var copyId = (await CopiesAsync()).Single().ExternalEventId!;

        var protector = new CalendarEventProtector(TelephonyTestHarness.Encryptor());
        await using (var db = _factory.CreateContext())
        {
            var calendar = await db.ConnectedCalendars.SingleAsync(c => c.Id == main);
            foreach (var (externalId, title) in new[] { (copyId, "Point dossier"), ("evt-own", "Comité de crédit") })
            {
                var e = ConnectedCalendarEvent.Create(_tenant, calendar, externalId, _clock.GetUtcNow());
                e.Apply(appointment.StartAt, appointment.EndAt, false, CalendarShowAs.Busy, protector.Protect(title), null, _clock.GetUtcNow());
                db.ConnectedCalendarEvents.Add(e);
            }

            await db.SaveChangesAsync();
        }

        var me = Substitute.For<ICurrentUser>();
        me.Id.Returns(_organizer);
        await using var read = _factory.CreateContext();
        var events = await new ListMyCalendarEventsHandler(read, me, protector)
            .Handle(new ListMyCalendarEventsQuery(appointment.StartAt.AddDays(-1), appointment.StartAt.AddDays(1)), CancellationToken.None);

        events.Value!.Select(e => e.Title).Should().Equal("Comité de crédit");
    }
}
