namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Features.Telephony.Recordings;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Kernel;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// Who may hear a recording (TEL-B-17).
///
/// <para>
/// The permission is not the whole authorization, and this is the surface where that matters
/// most: what is served is the actual audio of a conversation. These cases pin the four arms of
/// the rule and, as much, the one thing the rule must not do — tell a caller that a colleague's
/// conversation exists.
/// </para>
/// </summary>
public sealed class RecordingAccessTests : IDisposable
{
    private const string TestKey = "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU2Nzg5MDE=";

    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly InMemoryBackend _backend = new();

    public RecordingAccessTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task I_may_hear_my_own_call_and_the_access_is_logged()
    {
        var callId = await SeedAsync(crmUserId: _me);

        var result = await ListenAsync(callId);

        result.Should().BeAssignableTo<IFileHttpResult>();

        await using var db = _factory.CreateContext();
        var access = db.TelRecordingAccessLogs.Should().ContainSingle().Subject;

        access.ActorUserId.Should().Be(_me);
        access.Kind.Should().Be(TelRecordingAccessKind.Listen);
        access.CallId.Should().Be(callId);
    }

    [Fact]
    public async Task A_call_attributed_to_NOBODY_is_the_agencys_to_hear()
    {
        var callId = await SeedAsync(crmUserId: null);

        // The same set the missed-call list deliberately shows everyone: a voicemail left for the
        // branch has no owner and somebody has to be able to play it.
        (await ListenAsync(callId)).Should().BeAssignableTo<IFileHttpResult>();
    }

    [Fact]
    public async Task A_COLLEAGUES_call_about_a_record_outside_my_perimeter_is_refused()
    {
        var callId = await SeedAsync(crmUserId: _colleague, entityId: _clientId);

        var result = await ListenAsync(callId, recordAllowed: false);

        // 404 and not 403: a 403 confirms that a colleague had this conversation.
        await AssertNotFoundAsync(result);

        await using var db = _factory.CreateContext();

        // And nothing is logged as an access, because none happened — the refusal is a warning in
        // the log, not a row in the access trail.
        db.TelRecordingAccessLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_COLLEAGUES_call_about_a_record_I_may_work_is_allowed()
    {
        var callId = await SeedAsync(crmUserId: _colleague, entityId: _clientId);

        // A branch manager reviewing their own agent's conversation with their own branch's
        // customer. "Own calls only" would send them to a compliance officer for it.
        var result = await ListenAsync(callId, recordAllowed: true);

        result.Should().BeAssignableTo<IFileHttpResult>();
    }

    [Fact]
    public async Task A_COLLEAGUES_UNATTACHED_call_with_no_agency_is_refused()
    {
        var callId = await SeedAsync(crmUserId: _colleague, entityId: null, agencyId: null);

        // No record to ask about and no agency to place it in. It has an owner who is not me, so
        // nothing grants a route — and a call inside no restricted perimeter must not be granted
        // to every branch rather than to none.
        await AssertNotFoundAsync(
            await ListenAsync(callId, recordAllowed: true, perimeterAgencies: [Guid.NewGuid()]));
    }

    [Fact]
    public async Task A_SUPERVISOR_may_hear_a_colleagues_UNATTACHED_call_of_their_own_agency()
    {
        var branch = Guid.NewGuid();

        var callId = await SeedAsync(
            crmUserId: _colleague, entityId: null, agencyId: branch);

        // The gap TEL-B-19 closes. Before the call carried an agency the only route to a
        // colleague's conversation was the record it was attached to, so an UNATTACHED one was
        // audible to compliance alone — strict enough that a branch manager investigating a
        // complaint about their own agent's call had no way to hear it.
        //
        // recordAllowed is false on purpose: this must pass on the agency arm alone.
        var result = await ListenAsync(
            callId, recordAllowed: false, perimeterAgencies: [branch]);

        result.Should().BeAssignableTo<IFileHttpResult>();
    }

    [Fact]
    public async Task A_supervisor_of_ANOTHER_branch_may_not()
    {
        var callId = await SeedAsync(
            crmUserId: _colleague, entityId: null, agencyId: Guid.NewGuid());

        await AssertNotFoundAsync(
            await ListenAsync(
                callId, recordAllowed: false, perimeterAgencies: [Guid.NewGuid()]));
    }

    [Fact]
    public async Task A_compliance_officer_may_hear_a_call_outside_every_perimeter()
    {
        var callId = await SeedAsync(crmUserId: _colleague, entityId: _clientId);

        // The case the agency perimeter cannot express: settling a dispute.
        var result = await ListenAsync(
            callId, recordAllowed: false, compliance: true);

        result.Should().BeAssignableTo<IFileHttpResult>();
    }

    [Fact]
    public async Task An_EXPORT_follows_the_same_rule_and_still_demands_a_reason()
    {
        var callId = await SeedAsync(crmUserId: _colleague, entityId: _clientId);

        // Refused for the same reason as the listen — one rule, in one place, so the two cannot
        // differ about it.
        await AssertNotFoundAsync(
            await ExportAsync(callId, reason: "Litige 2026-114", recordAllowed: false));

        // And the reason is still mandatory on the arm that passes.
        var noReason = await ExportAsync(callId, reason: null, recordAllowed: true);

        await AssertErrorAsync(noReason, TelephonyErrors.RecordingExportReasonRequired);

        var exported = await ExportAsync(
            callId, reason: "Litige 2026-114", recordAllowed: true);

        exported.Should().BeAssignableTo<IFileHttpResult>();

        await using var db = _factory.CreateContext();
        var access = db.TelRecordingAccessLogs.Should().ContainSingle().Subject;

        access.Kind.Should().Be(TelRecordingAccessKind.Export);
        access.Reason.Should().Be("Litige 2026-114");
    }

    [Fact]
    public async Task A_call_with_no_recording_and_one_I_may_not_hear_answer_THE_SAME_thing()
    {
        var mine = await SeedAsync(crmUserId: _me, withRecording: false);
        var theirs = await SeedAsync(crmUserId: _colleague, entityId: _clientId);

        // Indistinguishable on purpose: if "no recording" and "not yours" differed, the pair would
        // tell a caller which of a colleague's calls were recorded.
        await AssertNotFoundAsync(await ListenAsync(mine));
        await AssertNotFoundAsync(await ListenAsync(theirs, recordAllowed: false));
    }

    [Fact]
    public async Task A_purged_recording_says_so_rather_than_pretending_it_never_existed()
    {
        var callId = await SeedAsync(crmUserId: _me);

        await using (var db = _factory.CreateContext())
        {
            var recording = await db.TelRecordings.AsTracking().SingleAsync(CancellationToken.None);
            recording.Purge(Now);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // "Being fetched", "retrieval failed" and "gone for ever" are all answers an agent needs,
        // and a 404 for all three would have them report a bug for a purge working as designed.
        await AssertErrorAsync(
            await ListenAsync(callId), TelephonyErrors.RecordingNotAvailable);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAsync(
        Guid? crmUserId, Guid? entityId = null, bool withRecording = true, Guid? agencyId = null)
    {
        await using var db = _factory.CreateContext();

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect("+22507111111");
        var to = protector.Protect("+22507000001");

        var call = TelCall.Open(
            _tenant, _connection, $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, TelCallStatus.Completed,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            Now.AddHours(-1), externalUserId: null, crmUserId: crmUserId,
            clickToCallId: null, agencyId: agencyId);

        if (entityId is { } id)
        {
            call.Attach(
                TelMatchStatus.Matched, TelMatchMethod.Number, TelEntityTypes.Customer, id);
        }

        db.TelCalls.Add(call);

        if (withRecording)
        {
            var stored = await Store().StoreAsync(
                _tenant, call.Id, [1, 2, 3, 4], "audio/mpeg", CancellationToken.None);

            var recording = TelRecording.Expect(
                _tenant, call.Id, "Obligation légale CIMA", Now.AddDays(90));

            recording.MarkStored(
                stored.StorageRef, Convert.FromHexString(stored.Sha256), 90, Now);

            db.TelRecordings.Add(recording);
            call.SetRecordingStatus(TelRecordingStatus.Available);
        }

        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private IRecordingStore Store() => new EncryptedRecordingStore(
        _backend,
        Options.Create(new RecordingStorageOptions { EncryptionKey = TestKey }),
        NullLogger<EncryptedRecordingStore>.Instance);

    private Task<IResult> ListenAsync(
        Guid callId,
        bool recordAllowed = true,
        bool compliance = false,
        Guid[]? perimeterAgencies = null)
        => ServeAsync(
            callId, null, TelRecordingAccessKind.Listen, recordAllowed, compliance,
            perimeterAgencies);

    private Task<IResult> ExportAsync(
        Guid callId, string? reason, bool recordAllowed = true, bool compliance = false)
        => ServeAsync(callId, reason, TelRecordingAccessKind.Export, recordAllowed, compliance);

    /// <summary>
    /// Calls the endpoint's own handler, not a copy of its branch: the two are <c>internal</c> for
    /// exactly this, the way <c>ReadKycDocumentEndpoint.Handle</c> is.
    /// </summary>
    private async Task<IResult> ServeAsync(
        Guid callId,
        string? reason,
        TelRecordingAccessKind kind,
        bool recordAllowed,
        bool compliance,
        Guid[]? perimeterAgencies = null)
    {
        await using var db = _factory.CreateContext();

        var http = new DefaultHttpContext();

        var claims = new List<Claim>();

        if (compliance)
            claims.Add(new Claim("permission", Permissions.CanManageCompliance.Code));

        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestScheme"));

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        var store = Store();
        var access = TelephonyTestHarness.RecordAccess(recordAllowed);

        // Empty by default — a listener who supervises nothing — so every case written before
        // TEL-B-19 keeps asserting what it was written to assert.
        var perimeter = TelephonyTestHarness.Perimeter(perimeterAgencies ?? []);
        var user = TelephonyTestHarness.User(_tenant, _me);
        var clock = new TelephonyTestHarness.FixedClock(Now);

        return kind == TelRecordingAccessKind.Listen
            ? await RecordingAccessEndpoints.ListenAsync(
                callId, reason, db, store, access, perimeter, user, accessor, clock,
                NullLoggerFactory.Instance, CancellationToken.None)
            : await RecordingAccessEndpoints.ExportAsync(
                callId, reason, db, store, access, perimeter, user, accessor, clock,
                NullLoggerFactory.Instance, CancellationToken.None);
    }

    private static async Task AssertNotFoundAsync(IResult result)
        => await AssertErrorAsync(result, TelephonyErrors.RecordingNotFound);

    /// <summary>
    /// Both the 404 and the 422 carry <c>{ error = code }</c>, so one assertion reads the code off
    /// whichever it is — which is what the cases above are actually about.
    /// </summary>
    private static Task AssertErrorAsync(IResult result, string expected)
    {
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Subject.Value;

        value!.GetType().GetProperty("error")!.GetValue(value)
            .Should().Be(expected);

        return Task.CompletedTask;
    }

    private sealed class InMemoryBackend : IObjectBackend
    {
        private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

        public Task PutAsync(string key, byte[] content, CancellationToken ct)
        {
            _objects[key] = content;

            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, long maxBytes, CancellationToken ct)
            => Task.FromResult(_objects.TryGetValue(key, out var bytes) ? bytes : null);

        public Task<bool> DeleteAsync(string key, CancellationToken ct)
            => Task.FromResult(_objects.Remove(key));

        public Task<bool> ExistsAsync(string key, CancellationToken ct)
            => Task.FromResult(_objects.ContainsKey(key));

        public async IAsyncEnumerable<string> ListAsync(
            string keyPrefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var key in _objects.Keys.ToList())
            {
                ct.ThrowIfCancellationRequested();

                yield return key;
            }

            await Task.CompletedTask;
        }
    }
}
