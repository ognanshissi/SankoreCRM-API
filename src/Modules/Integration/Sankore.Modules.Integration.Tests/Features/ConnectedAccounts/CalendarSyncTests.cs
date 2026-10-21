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
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>Connected agendas, lot 2: reading the agendas, incrementally, privately.</summary>
public sealed class CalendarSyncTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly InMemorySecrets _secrets = new();
    private readonly CalendarEventProtector _protector = new(TelephonyTestHarness.Encryptor());
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly ScriptedCalendars _provider = new();
    private Guid _accountId;

    public CalendarSyncTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);
        _currentUser.Id.Returns(_me);
        _currentUser.TenantId.Returns(_tenant);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InMemorySecrets : ISecretsModule
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

    /// <summary>Agendas and pages scripted per call; records what it was asked.</summary>
    private sealed class ScriptedCalendars : IConnectedAccountProvider
    {
        public List<ProviderCalendar> Calendars { get; } =
        [
            new("cal-work", "Calendrier", "#2563EB", true, false, false),
            new("cal-holidays", "Jours fériés", "#16A34A", false, true, true),
        ];

        /// <summary>Per calendar: the next pages to answer, in order.</summary>
        public Dictionary<string, Queue<ProviderEventPage>> Pages { get; } = [];

        public List<(string Calendar, string? Cursor)> Reads { get; } = [];

        public bool RevokeOnList { get; set; }

        public ConnectedAccountProvider Provider => ConnectedAccountProvider.Google;
        public bool IsAvailable => true;
        public Uri BuildAuthorizationUrl(string state, string codeChallenge, Uri redirectUri) => redirectUri;
        public Task<ProviderTokens> ExchangeCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderTokens> RefreshAsync(string refreshToken, CancellationToken ct) =>
            Task.FromResult(new ProviderTokens("access", null, DateTimeOffset.UtcNow.AddHours(1), ""));
        public Task<ProviderProfile> GetProfileAsync(string accessToken, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeAsync(string refreshToken, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<ProviderCalendar>> ListCalendarsAsync(string accessToken, CancellationToken ct)
        {
            if (RevokeOnList) throw new ProviderAuthException("calendars_http_401", revoked: true);
            return Task.FromResult<IReadOnlyList<ProviderCalendar>>(Calendars.ToList());
        }

        public Task<ProviderEventPage> ReadEventsAsync(
            string accessToken, string calendarId, string? cursor, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken ct)
        {
            Reads.Add((calendarId, cursor));
            return Task.FromResult(Pages.TryGetValue(calendarId, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : new ProviderEventPage([], [], null, $"delta-{calendarId}-{Reads.Count}"));
        }

        public Task<string> CreateEventAsync(string accessToken, string calendarId, ProviderEventDraft draft, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> UpdateEventAsync(string accessToken, string calendarId, string eventId, ProviderEventDraft draft, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteEventAsync(string accessToken, string calendarId, string eventId, CancellationToken ct) => throw new NotSupportedException();
    }

    private ProviderEvent Event(string id, string title, int dayOffset, int hour = 9, double hours = 1)
    {
        // Midnight UTC of the clock's day — not `Now.Date`, a DateTime the machine's offset would shift.
        var day = new DateTimeOffset(_clock.Now.UtcDateTime.Date, TimeSpan.Zero).AddDays(dayOffset);
        return new(id, title, "Siège", day.AddHours(hour), day.AddHours(hour + hours), false, CalendarShowAs.Busy);
    }

    // ── Reading ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_first_sync_discovers_every_agenda_ticked_and_stores_the_events_encrypted()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([
            new ProviderEventPage([Event("e1", "Comité de crédit", 1)], [], "page-2", null),
            new ProviderEventPage([Event("e2", "Revue du portefeuille", 2)], [], null, "delta-1"),
        ]);

        var outcome = await SyncAsync();

        outcome.Calendars.Should().Be(2);
        outcome.Upserted.Should().Be(2);

        await using var db = _factory.CreateContext();
        var calendars = await db.ConnectedCalendars.ToListAsync();
        calendars.Should().OnlyContain(c => c.IsSelected);
        calendars.Single(c => c.ExternalCalendarId == "cal-holidays").IsSubscription.Should().BeTrue();

        var work = calendars.Single(c => c.ExternalCalendarId == "cal-work");
        work.SyncCursor.Should().Be("delta-1");
        work.EventCount.Should().Be(2);

        var stored = await db.ConnectedCalendarEvents.SingleAsync(e => e.ExternalEventId == "e1");
        stored.TitleEnc.Should().NotBeNull().And.NotContain("Comité", "the title is ciphertext at rest");
        _protector.Reveal(stored.TitleEnc).Should().Be("Comité de crédit");

        _provider.Reads.Where(r => r.Calendar == "cal-work").Select(r => r.Cursor).Should().Equal(null, "page-2");
    }

    [Fact]
    public async Task The_next_sync_applies_only_the_changes_and_the_removals()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité", 1), Event("e2", "Revue", 2)], [], null, "delta-1")]);
        await SyncAsync();

        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité déplacé", 1, hour: 14)], ["e2"], null, "delta-2")]);
        var outcome = await SyncAsync();

        outcome.Removed.Should().Be(1);
        _provider.Reads.Last(r => r.Calendar == "cal-work").Cursor.Should().Be("delta-1");

        await using var db = _factory.CreateContext();
        var events = await db.ConnectedCalendarEvents.Where(e => e.ExternalEventId == "e1" || e.ExternalEventId == "e2").ToListAsync();
        events.Should().ContainSingle();
        _protector.Reveal(events[0].TitleEnc).Should().Be("Comité déplacé");
        events[0].StartAt.Hour.Should().Be(14);
    }

    [Fact]
    public async Task A_forgotten_cursor_and_a_stale_window_both_reread_the_agenda_and_sweep_what_is_gone()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité", 1), Event("e2", "Revue", 2)], [], null, "delta-1")]);
        await SyncAsync();

        // The provider forgot the cursor: the same run reads the window again, and e2 is no longer in it.
        _provider.Pages["cal-work"] = new([
            new ProviderEventPage([], [], null, null, CursorExpired: true),
            new ProviderEventPage([Event("e1", "Comité", 1)], [], null, "delta-full"),
        ]);
        await SyncAsync();

        await using (var db = _factory.CreateContext())
        {
            (await db.ConnectedCalendarEvents.Select(e => e.ExternalEventId).ToListAsync()).Should().Equal("e1");
            (await db.ConnectedCalendars.SingleAsync(c => c.ExternalCalendarId == "cal-work")).SyncCursor.Should().Be("delta-full");
        }

        // Eight days later the window has drifted: a full read, without a cursor.
        _clock.Now = _clock.Now.AddDays(8);
        await SyncAsync();
        _provider.Reads.Last(r => r.Calendar == "cal-work").Cursor.Should().BeNull();
    }

    [Fact]
    public async Task An_incremental_change_outside_the_window_is_not_kept()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité", 1)], [], null, "delta-1")]);
        await SyncAsync();

        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité reporté à l'an prochain", 400)], [], null, "delta-2")]);
        await SyncAsync();

        await using var db = _factory.CreateContext();
        (await db.ConnectedCalendarEvents.AnyAsync(e => e.ExternalEventId == "e1")).Should().BeFalse();
    }

    [Fact]
    public async Task An_agenda_the_provider_no_longer_lists_goes_with_its_events()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-holidays"] = new([new ProviderEventPage([Event("h1", "Férié", 3)], [], null, "delta-h")]);
        await SyncAsync();

        _provider.Calendars.RemoveAll(c => c.Id == "cal-holidays");
        await SyncAsync();

        await using var db = _factory.CreateContext();
        (await db.ConnectedCalendars.AnyAsync(c => c.ExternalCalendarId == "cal-holidays")).Should().BeFalse();
        (await db.ConnectedCalendarEvents.AnyAsync(e => e.ExternalEventId == "h1")).Should().BeFalse();
    }

    [Fact]
    public async Task A_provider_that_refuses_the_account_marks_it_to_reconnect()
    {
        await SeedAccountAsync();
        _provider.RevokeOnList = true;

        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.AsTracking().SingleAsync();
        (await Sync(db).SyncAccountAsync(account, CancellationToken.None)).Error.Should().Be(ConnectedAccountErrors.NeedsReconnect);
        account.Status.Should().Be(ConnectedAccountStatus.NeedsReconnect);
    }

    // ── Ticking, reading back, removing ────────────────────────────────────────

    [Fact]
    public async Task Unticking_an_agenda_deletes_its_events_and_ticking_it_reads_it_again_in_the_background()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité", 1)], [], null, "delta-1")]);
        await SyncAsync();

        Guid calendarId;
        await using (var read = _factory.CreateContext())
            calendarId = (await read.ConnectedCalendars.SingleAsync(c => c.ExternalCalendarId == "cal-work")).Id;

        var jobs = Substitute.For<IBackgroundJobClient>();

        await using (var db = _factory.CreateContext())
        {
            (await new SelectConnectedCalendarHandler(db, _currentUser, Sync(db), jobs)
                .Handle(new SelectConnectedCalendarCommand(_accountId, calendarId, false), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        await using (var db = _factory.CreateContext())
        {
            (await db.ConnectedCalendarEvents.AnyAsync(e => e.CalendarId == calendarId)).Should().BeFalse();
            var calendar = await db.ConnectedCalendars.SingleAsync(c => c.Id == calendarId);
            calendar.IsSelected.Should().BeFalse();
            calendar.SyncCursor.Should().BeNull();

            (await new SelectConnectedCalendarHandler(db, _currentUser, Sync(db), jobs)
                .Handle(new SelectConnectedCalendarCommand(_accountId, calendarId, true), CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        jobs.ReceivedCalls().Should().ContainSingle(c => c.GetMethodInfo().Name == nameof(IBackgroundJobClient.Create));
    }

    [Fact]
    public async Task My_events_come_back_decrypted_and_a_colleague_gets_none_of_them()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité de crédit", 1)], [], null, "delta-1")]);
        await SyncAsync();

        await using var db = _factory.CreateContext();
        var range = new ListMyCalendarEventsQuery(_clock.Now, _clock.Now.AddDays(7));

        var mine = (await new ListMyCalendarEventsHandler(db, _currentUser, _protector).Handle(range, CancellationToken.None)).Value!;
        mine.Should().ContainSingle();
        mine[0].Title.Should().Be("Comité de crédit");
        mine[0].Location.Should().Be("Siège");
        mine[0].CalendarName.Should().Be("Calendrier");

        var colleague = Substitute.For<ICurrentUser>();
        colleague.Id.Returns(Guid.NewGuid());
        colleague.TenantId.Returns(_tenant);
        (await new ListMyCalendarEventsHandler(db, colleague, _protector).Handle(range, CancellationToken.None)).Value.Should().BeEmpty();
    }

    [Fact]
    public async Task Removing_the_account_removes_its_agendas_and_their_events()
    {
        await SeedAccountAsync();
        _provider.Pages["cal-work"] = new([new ProviderEventPage([Event("e1", "Comité", 1)], [], null, "delta-1")]);
        await SyncAsync();

        await using var db = _factory.CreateContext();
        (await new RemoveConnectedAccountHandler(db, _currentUser, _secrets, Providers(), NoCopies(db), NullLogger<RemoveConnectedAccountHandler>.Instance)
            .Handle(new RemoveConnectedAccountCommand(_accountId), CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await db.ConnectedCalendars.CountAsync()).Should().Be(0);
        (await db.ConnectedCalendarEvents.CountAsync()).Should().Be(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private ConnectedAccountProviders Providers() => new([_provider]);

    private ConnectedCalendarSync Sync(IntegrationDbContext db) => new(
        db,
        new ConnectedAccountTokens(db, _secrets, Providers(), NullLogger<ConnectedAccountTokens>.Instance),
        Providers(),
        _protector,
        _clock,
        NullLogger<ConnectedCalendarSync>.Instance);

    private async Task SeedAccountAsync()
    {
        await using var db = _factory.CreateContext();
        var account = ConnectedAccount.Connect(_tenant, _me, ConnectedAccountProvider.Google, "ext-1", "awa@example.com", "Awa", "Personnel", "", _clock.Now);
        db.ConnectedAccounts.Add(account);
        await db.SaveChangesAsync();
        _accountId = account.Id;

        await _secrets.SetAsync(ConnectedAccountSecrets.AccessToken(_tenant, account.Id), "access", DateTimeOffset.UtcNow.AddHours(1));
        await _secrets.SetAsync(ConnectedAccountSecrets.RefreshToken(_tenant, account.Id), "refresh");
    }

    private async Task<CalendarSyncOutcome> SyncAsync()
    {
        await using var db = _factory.CreateContext();
        var account = await db.ConnectedAccounts.AsTracking().SingleAsync();
        var result = await Sync(db).SyncAccountAsync(account, CancellationToken.None);
        result.IsSuccess.Should().BeTrue(result.Error);
        return result.Value!;
    }

    private AppointmentCalendarCopies NoCopies(IntegrationDbContext db) => new(
        db, Substitute.For<Sankore.Modules.Leads.PublicApi.ILeadsModule>(), Providers(),
        new ConnectedAccountTokens(db, _secrets, Providers(), NullLogger<ConnectedAccountTokens>.Instance),
        TimeProvider.System, NullLogger<AppointmentCalendarCopies>.Instance);
}
