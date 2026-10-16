namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Xunit;

/// <summary>
/// The sweep that finds the calls whose webhook was lost (TEL-B-14).
///
/// <para>
/// The property that matters is the cursor's: the calls commit, THEN the cursor, in a separate
/// transaction — and a failure leaves the cursor where it was so the window is re-read. The
/// opposite loses calls silently, because nothing afterwards knows they were skipped.
/// </para>
/// </summary>
public sealed class TelephonyCatchUpTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyCatchUpTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_call_the_webhook_never_delivered_is_journalled()
    {
        // The fake's seeded call started at its own fixed instant; the sweep's first run looks an
        // hour back from the clock, which is after it — so the clock is moved to just after the
        // call rather than the fixture being bent.
        var report = await RunAsync(FakeAdapter.SeededCallStartedAt.AddMinutes(5));

        report.Journalled.Should().Be(1);

        await using var db = _factory.CreateContext();
        db.TelCalls.Single().ExternalCallId.Should().Be("fake-call-1");
    }

    [Fact]
    public async Task The_cursor_advances_to_the_run_start_MINUS_the_overlap()
    {
        var now = FakeAdapter.SeededCallStartedAt.AddMinutes(5);

        await RunAsync(now);

        await using var db = _factory.CreateContext();
        var cursor = db.SyncCursors.Single();

        cursor.Stream.Should().Be(SyncStream.Calls);

        var parsed = DateTimeOffset.Parse(
            cursor.Cursor!, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

        // A call is listed by the instant it STARTED, so one that started at 10:29 and ended at
        // 10:34 may be absent from a listing taken at 10:30 — or carry a status that is not final.
        // A cursor at the run's start would step over exactly those.
        parsed.Should().Be(now - SyncTelephonyCallsJob.Overlap);
    }

    [Fact]
    public async Task A_second_run_converges_on_one_row_per_provider_call()
    {
        var now = FakeAdapter.SeededCallStartedAt.AddMinutes(5);

        await RunAsync(now);
        await RunAsync(now.AddMinutes(1));

        await using var db = _factory.CreateContext();

        // Re-reading is free precisely because of this: ux_tel_call_external makes the webhook and
        // the sweep converge, so the overlap costs nothing.
        db.TelCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_provider_failure_leaves_the_cursor_where_it_was_and_records_it()
    {
        var now = FakeAdapter.SeededCallStartedAt.AddMinutes(5);

        // One good run to establish a cursor.
        await RunAsync(now);

        var before = await CursorAsync();

        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout),
        };

        var report = await RunAsync(now.AddHours(1), adapter);

        report.Failed.Should().BeTrue();

        var after = await CursorAsync();

        // Unchanged, so the next run re-reads the window. An advanced cursor would mean the calls
        // of that hour are skipped for ever, with nothing to say they were.
        after.Cursor.Should().Be(before.Cursor);
        after.LastError.Should().NotBeNullOrWhiteSpace();
        after.ConsecutiveFailures.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_tenant_with_no_telephony_is_skipped_and_writes_no_cursor()
    {
        var report = await RunAsync(FakeAdapter.SeededCallStartedAt, noAdapter: true);

        report.SkippedBecause.Should().Be(TelephonyErrors.Disabled);

        await using var db = _factory.CreateContext();

        // A run that did not happen must not look like one that did.
        db.SyncCursors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_connection_that_is_no_longer_the_active_one_is_skipped()
    {
        // A tenant that replaced its provider leaves the old row inactive, and the fan-out is per
        // connection — sweeping the wrong one would journal its calls under this cursor.
        var report = await RunAsync(
            FakeAdapter.SeededCallStartedAt, connectionId: Guid.NewGuid());

        report.SkippedBecause.Should().Be("NOT_THE_ACTIVE_CONNECTION");
    }

    [Fact]
    public void The_first_run_reads_a_bounded_window_and_not_everything()
    {
        var cursor = IntegrationSyncCursor.Create(_tenant, _connection, SyncStream.Calls);
        var now = new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

        // A tenant switching telephony on does not want its first sweep to import a year of calls
        // nobody in the CRM can attach to anything.
        SyncTelephonyCallsJob.ReadFrom(cursor, now)
            .Should().Be(now - SyncTelephonyCallsJob.FirstRunLookback);
    }

    [Fact]
    public void A_cursor_this_module_cannot_parse_is_treated_as_absent()
    {
        var cursor = IntegrationSyncCursor.Create(_tenant, _connection, SyncStream.Calls);
        cursor.Advance("not an instant", TelephonyTestHarness.Clock);

        var now = new DateTimeOffset(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

        // The column is documented opaque. A run that refused to start because of one would stop
        // the sweep for ever on a value nobody can see; the lookback re-reads an hour instead,
        // which is the cheap direction.
        SyncTelephonyCallsJob.ReadFrom(cursor, now)
            .Should().Be(now - SyncTelephonyCallsJob.FirstRunLookback);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<IntegrationSyncCursor> CursorAsync()
    {
        await using var db = _factory.CreateContext();

        return await db.SyncCursors.SingleAsync(CancellationToken.None);
    }

    /// <param name="noAdapter">
    /// Exercises "the deployment shipped without one", which is also how a tenant with no
    /// telephony looks. A separate flag rather than a null adapter, so the ordinary overload can
    /// default the adapter and still be able to express its absence.
    /// </param>
    private async Task<TelephonyCatchUpReport> RunAsync(
        DateTimeOffset now,
        FakeAdapter? adapter = null,
        Guid? connectionId = null,
        bool noAdapter = false)
    {
        await using var db = _factory.CreateContext();

        var customers = Substitute.For<ICustomersModule>();
        var leads = Substitute.For<ILeadsModule>();

        customers.FindMatchesAsync(
                Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns([]);

        leads.FindOpenLeadsByPhoneAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        return await SyncTelephonyCallsJob.RunAsync(
            db,
            TelephonyTestHarness.Access(db, noAdapter ? null : adapter ?? new FakeAdapter()),
            new TelCallJournalWriter(
                db,
                TelephonyTestHarness.Numbers(),
                new TelephonyCallMatcher(
                    customers, leads, NullLogger<TelephonyCallMatcher>.Instance),
                NullLogger<TelCallJournalWriter>.Instance),
            new TelephonyTestHarness.FixedClock(now),
            NullLogger<SyncTelephonyCallsJob>.Instance,
            _tenant,
            connectionId ?? _connection,
            CancellationToken.None);
    }
}
