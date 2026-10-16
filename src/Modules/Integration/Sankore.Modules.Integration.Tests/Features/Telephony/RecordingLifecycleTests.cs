namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Recordings;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// A recording's whole life (TEL-B-16, TEL-B-17): fetched under a policy, held, purged — or
/// destroyed because the person was anonymised.
/// </summary>
public sealed class RecordingLifecycleTests : IDisposable
{
    private const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly InMemoryBackend _backend = new();

    public RecordingLifecycleTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── The gate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_tenant_that_keeps_nothing_fetches_nothing()
    {
        await SeedPendingCallAsync();

        var report = await FetchAsync();

        // The whole of "built but shipped disabled": every tenant is here until a compliance
        // officer states a basis.
        report.SkippedBecause.Should().Be(TelephonyErrors.RecordingDisabled);
        report.Stored.Should().Be(0);
        _backend.Objects.Should().BeEmpty();
    }

    [Fact]
    public async Task AllCalls_fetches_stores_and_freezes_the_basis_on_the_row()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        var call = await SeedPendingCallAsync();

        var report = await FetchAsync();

        report.Stored.Should().Be(1);

        await using var db = _factory.CreateContext();
        var recording = db.TelRecordings.Single();

        recording.CallId.Should().Be(call.Id);
        recording.Status.Should().Be(TelRecordingStatus.Available);
        recording.StorageRef.Should().NotBeNullOrWhiteSpace();
        recording.Sha256.Should().NotBeNullOrEmpty();

        // Frozen at retrieval, from the policy as it stood: a policy an institution changes in
        // March must not silently extend the retention of a recording made in January.
        recording.LegalBasis.Should().Be("Obligation légale CIMA");

        // Counted from the CALL and not from the retrieval — the retention is about the
        // conversation, not about our plumbing.
        recording.RetentionUntil.Should().Be(call.StartedAt.AddDays(90));

        // And the call now advertises a player.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.Available);

        _backend.Objects.Should().HaveCount(1);
    }

    [Fact]
    public async Task OnDemand_leaves_a_call_nobody_asked_about_alone()
    {
        await ConfigureAsync(TelRecordingMode.OnDemand);
        await SeedPendingCallAsync();

        var report = await FetchAsync();

        report.Stored.Should().Be(0);
        report.SkippedCalls.Should().Be(1);

        await using var db = _factory.CreateContext();

        // NotRecorded and not Failed: nothing went wrong, the institution chose not to keep it.
        // And not left Pending either, which would have every run re-examine it for ever.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.NotRecorded);
        db.TelRecordings.Should().BeEmpty();
    }

    [Fact]
    public async Task OnDemand_fetches_the_call_an_agent_asked_to_keep()
    {
        await ConfigureAsync(TelRecordingMode.OnDemand);

        var call = await SeedPendingCallAsync(keepRecording: true);

        (await FetchAsync()).Stored.Should().Be(1);

        await using var db = _factory.CreateContext();
        db.TelRecordings.Single().CallId.Should().Be(call.Id);
    }

    [Fact]
    public async Task A_call_over_the_duration_ceiling_is_not_fetched_and_says_so()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls, maxDurationSeconds: 60);

        await SeedPendingCallAsync(talkSeconds: 600);

        var report = await FetchAsync();

        report.Failed.Should().Be(1);
        _backend.Objects.Should().BeEmpty("nothing was downloaded");

        await using var db = _factory.CreateContext();

        // Failed and not silently skipped: an operator has to be able to see that a conversation
        // exists and was not kept.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.Failed);
    }

    [Fact]
    public async Task A_tenant_that_keeps_recordings_with_no_store_configured_fails_loudly()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();

        var report = await FetchAsync(noStore: true);

        // A deployment that switched recording on without configuring a key. The row says so
        // rather than the sweep quietly doing nothing.
        report.SkippedBecause.Should().Be("NO_STORE");
    }

    [Fact]
    public async Task A_transient_provider_failure_leaves_the_call_pending_for_the_next_run()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();

        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout),
        };

        var report = await FetchAsync(adapter);

        report.Stored.Should().Be(0);
        report.Failed.Should().Be(0);

        await using var db = _factory.CreateContext();

        // A recording is not ready the instant a call ends. Pending is the honest state.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.Pending);
    }

    [Fact]
    public async Task Fetching_twice_stores_one_object()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();

        await FetchAsync();
        var second = await FetchAsync();

        second.Stored.Should().Be(0, "the call is no longer Pending");

        await using var db = _factory.CreateContext();
        db.TelRecordings.Should().HaveCount(1);
        _backend.Objects.Should().HaveCount(1);
    }

    // ── The purge ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_recording_past_its_retention_is_destroyed_bytes_first()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();
        await FetchAsync();

        var purged = await PurgeAsync(Now.AddDays(200));

        purged.Should().Be(1);

        // The bytes went, which is the half that matters: a row marked purged whose object
        // survived is a file nobody will look for again, still readable on the volume.
        _backend.Objects.Should().BeEmpty();

        await using var db = _factory.CreateContext();
        var recording = db.TelRecordings.Single();

        recording.Status.Should().Be(TelRecordingStatus.Purged);
        recording.StorageRef.Should().BeNull();
        recording.PurgedAt.Should().NotBeNull();

        // And the call stops advertising a player.
        db.TelCalls.Single().RecordingStatus.Should().Be(TelRecordingStatus.Purged);
    }

    [Fact]
    public async Task A_recording_inside_its_retention_is_left_alone()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();
        await FetchAsync();

        (await PurgeAsync(Now.AddDays(10))).Should().Be(0);

        _backend.Objects.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_LEGAL_HOLD_stops_the_retention_purge()
    {
        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync();
        await FetchAsync();

        await using (var db = _factory.CreateContext())
        {
            var recording = await db.TelRecordings.AsTracking().SingleAsync(CancellationToken.None);
            recording.PlaceLegalHold("Litige en cours, dossier 2026-114.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        (await PurgeAsync(Now.AddDays(200))).Should().Be(0);

        _backend.Objects.Should().HaveCount(1, "the evidence a dispute needs is still there");
    }

    // ── The anonymisation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Anonymising_a_client_destroys_their_recordings_and_blanks_their_calls()
    {
        var clientId = Guid.NewGuid();

        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync(entityId: clientId);
        await FetchAsync();

        var purged = await AnonymiseAsync(clientId, Now.AddDays(1));

        purged.Should().Be(1);
        _backend.Objects.Should().BeEmpty();

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // The call STAYS, blanked: destroying the direction, the durations and the outcome would
        // turn a retention rule into a reporting outage.
        call.FromNumberEnc.Should().BeEmpty();
        call.ToNumberBi.Should().BeEmpty();
        call.Direction.Should().Be(TelCallDirection.Inbound);
        call.RecordingStatus.Should().Be(TelRecordingStatus.Purged);
    }

    [Fact]
    public async Task Anonymising_overrides_a_LEGAL_HOLD()
    {
        var clientId = Guid.NewGuid();

        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync(entityId: clientId);
        await FetchAsync();

        await using (var db = _factory.CreateContext())
        {
            var recording = await db.TelRecordings.AsTracking().SingleAsync(CancellationToken.None);
            recording.PlaceLegalHold("Litige en cours.");
            await db.SaveChangesAsync(CancellationToken.None);
        }

        var purged = await AnonymiseAsync(clientId, Now.AddDays(1));

        // A hold stops a routine EXPIRY. An anonymisation is a different instruction — a
        // right-to-erasure request, or a retention rule already decided for the whole person — and
        // honouring a hold against it would let one placed years ago quietly veto an erasure.
        purged.Should().Be(1);
        _backend.Objects.Should().BeEmpty();

        await using var after = _factory.CreateContext();
        var row = after.TelRecordings.Single();

        row.Status.Should().Be(TelRecordingStatus.Purged);

        // And the hold is lifted: a held recording that no longer exists would read, to whoever
        // placed the hold, as evidence still being kept for them.
        row.LegalHold.Should().BeFalse();
    }

    [Fact]
    public async Task Anonymising_a_client_with_no_calls_does_nothing_and_does_not_throw()
    {
        var act = async () => await AnonymiseAsync(Guid.NewGuid(), Now);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_redelivered_anonymisation_does_no_second_piece_of_work()
    {
        var clientId = Guid.NewGuid();

        await ConfigureAsync(TelRecordingMode.AllCalls);
        await SeedPendingCallAsync(entityId: clientId);
        await FetchAsync();

        (await AnonymiseAsync(clientId, Now.AddDays(1))).Should().Be(1);

        // Idempotent by construction: a purged recording has nothing left to destroy, so no inbox
        // table is needed.
        (await AnonymiseAsync(clientId, Now.AddDays(1))).Should().Be(0);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task ConfigureAsync(
        TelRecordingMode mode, int retentionDays = 90, int maxDurationSeconds = 3600)
    {
        await using var db = _factory.CreateContext();

        var result = await new SetRecordingPolicyHandler(
                db, TelephonyTestHarness.User(_tenant), new TelephonyTestHarness.FixedClock(Now))
            .Handle(
                new SetRecordingPolicyCommand(
                    mode, "Obligation légale CIMA", "Preuve de consentement",
                    retentionDays, DeleteAtProvider: false, maxDurationSeconds),
                CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    /// A call the journal has marked <c>Pending</c> — which is what it does when the provider says
    /// it HAS a recording. The external id is the fake adapter's seeded call, so the download
    /// resolves.
    /// </summary>
    private async Task<TelCall> SeedPendingCallAsync(
        bool keepRecording = false, int? talkSeconds = 90, Guid? entityId = null)
    {
        await using var db = _factory.CreateContext();

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");

        var call = TelCall.Open(
            _tenant, _connection, "fake-call-1",
            TelCallDirection.Inbound, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            Now.AddHours(-1));

        call.TryMoveTo(TelCallStatus.Completed, Now.AddMinutes(-50));
        call.RecordDurations(5, talkSeconds);
        call.SetRecordingStatus(TelRecordingStatus.Pending);

        if (keepRecording) call.MarkForRecording();

        if (entityId is { } id)
        {
            call.Attach(
                TelMatchStatus.Matched, TelMatchMethod.Number, TelEntityTypes.Customer, id);
        }

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call;
    }

    private IRecordingStore Store() => new EncryptedRecordingStore(
        _backend,
        Options.Create(new RecordingStorageOptions { EncryptionKey = TestKey }),
        NullLogger<EncryptedRecordingStore>.Instance);

    private async Task<RecordingFetchReport> FetchAsync(
        FakeAdapter? adapter = null, bool noStore = false)
    {
        await using var db = _factory.CreateContext();

        return await FetchTelephonyRecordingsJob.RunAsync(
            db,
            TelephonyTestHarness.Access(db, adapter ?? new FakeAdapter()),
            new TelephonyRecordingPolicyReader(db),
            noStore ? null : Store(),
            new TelephonyTestHarness.FixedClock(Now),
            NullLogger<FetchTelephonyRecordingsJob>.Instance,
            _tenant,
            CancellationToken.None);
    }

    private async Task<int> PurgeAsync(DateTimeOffset now)
    {
        await using var db = _factory.CreateContext();

        return await PurgeTelephonyRecordingsJob.RunAsync(
            db,
            Store(),
            new TelephonyTestHarness.FixedClock(now),
            NullLogger<PurgeTelephonyRecordingsJob>.Instance,
            _tenant,
            CancellationToken.None);
    }

    private async Task<int> AnonymiseAsync(Guid clientId, DateTimeOffset now)
    {
        await using var db = _factory.CreateContext();

        return await ClientAnonymizedRecordingConsumer.ApplyAsync(
            db,
            Store(),
            new TelephonyTestHarness.FixedClock(now),
            NullLogger<ClientAnonymizedRecordingConsumer>.Instance,
            _tenant,
            clientId,
            CancellationToken.None);
    }

    /// <summary>A backend whose objects a test can count — the only way to assert the bytes went.</summary>
    private sealed class InMemoryBackend : IObjectBackend
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] content, CancellationToken ct)
        {
            Objects[key] = content;

            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, long maxBytes, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(key, out var bytes) ? bytes : null);

        public Task<bool> DeleteAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.Remove(key));

        public Task<bool> ExistsAsync(string key, CancellationToken ct)
            => Task.FromResult(Objects.ContainsKey(key));

        public async IAsyncEnumerable<string> ListAsync(
            string keyPrefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var key in Objects.Keys
                .Where(k => k.StartsWith(keyPrefix, StringComparison.Ordinal))
                .ToList())
            {
                ct.ThrowIfCancellationRequested();

                yield return key;
            }

            await Task.CompletedTask;
        }
    }
}
