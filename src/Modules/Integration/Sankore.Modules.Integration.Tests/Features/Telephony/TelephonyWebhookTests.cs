namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Globalization;
using System.Text;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Sync;
using Sankore.Modules.Integration.Features.Telephony.Webhook;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The telephony webhook (TEL-B-07): what it accepts, what it refuses, and what a redelivery
/// costs.
///
/// <para>
/// Exercised through <c>AcceptAsync</c>, which takes no <c>HttpContext</c> — this repository has
/// no <c>WebApplicationFactory</c> precedent and the properties under test are not HTTP
/// properties.
/// </para>
/// </summary>
public sealed class TelephonyWebhookTests : IDisposable
{
    private const string Secret = "a-dev-only-webhook-secret-at-least-32-bytes";

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyWebhookTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_valid_signature_stores_the_event_and_queues_the_processing()
    {
        var body = Body("ringing:fake-call-1");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        await using var db = _factory.CreateContext();

        var outcome = await AcceptAsync(db, hangfire, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);

        var job = Jobs(hangfire).Single();

        // Four values and nothing of the call: Hangfire arguments are rows in the shared
        // application database and lines on a dashboard page.
        job.Args.Should().HaveCount(4);
        job.Args[0].Should().Be(_tenant);
        job.Args[1].Should().Be(_connection);

        await using var after = _factory.CreateContext();
        after.TelWebhookEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task The_stored_body_is_encrypted_and_carries_no_readable_number()
    {
        // tel_webhook_event is the one table of this family that would otherwise hold a readable
        // telephone number, in volume — and it is the table an operator opens when a call did not
        // land.
        var body = Body("ringing:fake-call-1");

        await using var db = _factory.CreateContext();
        await AcceptAsync(db, Substitute.For<IBackgroundJobClient>(), body, Signed(body));

        await using var after = _factory.CreateContext();
        var stored = after.TelWebhookEvents.Single();

        Encoding.UTF8.GetString(stored.PayloadEnc)
            .Should().StartWith("v1:").And.NotContain("fake-call-1");

        // And it opens again, byte for byte, for the one reader that needs it.
        TelephonyTestHarness.Payloads().Reveal(stored.PayloadEnc).Should().Equal(body);
    }

    [Fact]
    public async Task A_wrong_signature_is_refused_and_stores_nothing()
    {
        var body = Body("ringing:fake-call-1");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        await using var db = _factory.CreateContext();

        var outcome = await AcceptAsync(
            db, hangfire, body,
            SyncWebhookVerifier.Sign("some-other-secret-entirely", Unix(), body));

        outcome.Should().Be(WebhookOutcome.Rejected);
        Jobs(hangfire).Should().BeEmpty();

        await using var after = _factory.CreateContext();
        after.TelWebhookEvents.Should().BeEmpty("nothing is parsed before the signature is proven");
    }

    [Fact]
    public async Task A_connection_with_no_configured_secret_fails_closed()
    {
        var body = Body("ringing:fake-call-1");

        await using var db = _factory.CreateContext();

        // "No secret configured" must never read as "no signature required": that is an
        // unauthenticated write path into a tenant's call journal.
        var outcome = await AcceptAsync(
            db, Substitute.For<IBackgroundJobClient>(), body, Signed(body),
            secrets: Secrets(stored: null));

        outcome.Should().Be(WebhookOutcome.Rejected);
    }

    [Fact]
    public async Task A_core_banking_connection_is_refused_on_the_telephony_route()
    {
        // A signature valid for one family must not open another family's journal. The operator
        // who pasted the wrong URL into a provider's console learns nothing either way.
        await using var seed = _factory.CreateContext();

        var cbs = IntegrationConnection.Create(
            tenantId: _tenant,
            family: IntegrationFamily.CoreBanking,
            kind: IntegrationKind.Fake,
            mode: IntegrationMode.Api,
            name: "CBS",
            settings: new FakeSettings { Family = IntegrationFamily.CoreBanking },
            createdBy: TelephonyTestHarness.Actor,
            clock: TelephonyTestHarness.Clock);

        cbs.RecordHealth(
            IntegrationHealth.Healthy(TimeSpan.FromMilliseconds(10), TelephonyTestHarness.Now),
            TelephonyTestHarness.Clock);
        cbs.Activate(TelephonyTestHarness.Actor, TelephonyTestHarness.Clock);

        seed.Connections.Add(cbs);
        await seed.SaveChangesAsync(CancellationToken.None);

        var body = Body("ringing:fake-call-1");

        await using var db = _factory.CreateContext();

        var outcome = await AcceptAsync(db, Substitute.For<IBackgroundJobClient>(), body,
            Signed(body), connectionId: cbs.Id);

        outcome.Should().Be(WebhookOutcome.Rejected);
    }

    [Fact]
    public async Task An_inactive_connection_is_refused_rather_than_reported_as_unknown()
    {
        await using (var seed = _factory.CreateContext())
        {
            var connection = await seed.Connections.AsTracking().SingleAsync(CancellationToken.None);
            connection.Deactivate(TelephonyTestHarness.Actor, TelephonyTestHarness.Clock);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        var body = Body("ringing:fake-call-1");

        await using var db = _factory.CreateContext();

        // Deactivating a connection must stop its webhook URL working, and answering differently
        // would say that the connection exists.
        var outcome = await AcceptAsync(db, Substitute.For<IBackgroundJobClient>(), body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Rejected);
    }

    [Fact]
    public async Task An_unknown_connection_id_is_refused_and_not_reported_as_absent()
    {
        var body = Body("ringing:fake-call-1");

        await using var db = _factory.CreateContext();

        // A 404 here would make the endpoint a tenant-enumeration oracle: an inventory of which
        // institutions run telephony and with whom.
        var outcome = await AcceptAsync(
            db, Substitute.For<IBackgroundJobClient>(), body, Signed(body),
            connectionId: Guid.NewGuid());

        outcome.Should().Be(WebhookOutcome.Rejected);
    }

    [Fact]
    public async Task A_redelivered_event_is_accepted_and_does_no_second_piece_of_work()
    {
        var body = Body("ringing:fake-call-1");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        await using (var db = _factory.CreateContext())
            await AcceptAsync(db, hangfire, body, Signed(body));

        await using (var db = _factory.CreateContext())
        {
            var outcome = await AcceptAsync(db, hangfire, body, Signed(body));

            // This is what closes the replay hole SyncWebhookVerifier documents about itself: the
            // insert IS the check, and a telephony event triggers a status transition rather than
            // an idempotent re-projection.
            outcome.Should().Be(WebhookOutcome.Accepted);
        }

        Jobs(hangfire).Should().HaveCount(1);

        await using var after = _factory.CreateContext();
        after.TelWebhookEvents.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_verified_body_the_adapter_cannot_read_is_accepted_and_ignored()
    {
        // The signature already proved the sender, and a sender told 4xx redelivers the same body
        // on a schedule for ever.
        var body = Body("}{ not a payload");
        var hangfire = Substitute.For<IBackgroundJobClient>();

        await using var db = _factory.CreateContext();

        var outcome = await AcceptAsync(db, hangfire, body, Signed(body));

        outcome.Should().Be(WebhookOutcome.Accepted);
        Jobs(hangfire).Should().BeEmpty("there is nothing to re-read");

        await using var after = _factory.CreateContext();
        var stored = after.TelWebhookEvents.Single();

        // Stored and FINISHED, not left pending: an event nothing is coming for would make "what
        // is still pending" unreadable, and the body stays for whoever has to diagnose a provider
        // emitting events this module cannot act on.
        stored.ProcessedAt.Should().NotBeNull();
        stored.Outcome.Should().Be("SKIPPED:NO_CALL_ID");
    }

    [Fact]
    public async Task A_replayed_timestamp_is_refused()
    {
        var body = Body("ringing:fake-call-1");

        // An authentic request minted twenty minutes ago against a five-minute window: the bytes
        // and the signature are genuine, only the clock refuses it.
        var mintedAt = TelephonyTestHarness.Now.AddMinutes(-20);
        var stale = mintedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        await using var db = _factory.CreateContext();

        var outcome = await AcceptAsync(
            db, Substitute.For<IBackgroundJobClient>(), body,
            SyncWebhookVerifier.Sign(Secret, stale, body), timestamp: stale);

        outcome.Should().Be(WebhookOutcome.Rejected);
    }

    [Fact]
    public async Task The_secret_is_read_under_the_tenant_the_connection_belongs_to()
    {
        // The request carries no JWT, so the tenant can only come from the row. A secret keyed by
        // anything else would either never be found or — worse — be another tenant's.
        var body = Body("ringing:fake-call-1");
        var secrets = Secrets();

        await using var db = _factory.CreateContext();

        await AcceptAsync(db, Substitute.For<IBackgroundJobClient>(), body, Signed(body),
            secrets: secrets);

        await secrets.Received(1).GetValueAsync(
            Arg.Is<SecretKey>(k =>
                k.TenantId == _tenant
                && k.EntityId == _connection
                && k.Scope == "integration"
                && k.Name == "webhook-secret"),
            Arg.Any<CancellationToken>());
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private static byte[] Body(string payload) => Encoding.UTF8.GetBytes(payload);

    private static string Unix() => TelephonyTestHarness.Now.ToUnixTimeSeconds()
        .ToString(CultureInfo.InvariantCulture);

    private static string Signed(byte[] body) => SyncWebhookVerifier.Sign(Secret, Unix(), body);

    private static ISecretsModule Secrets(string? stored = Secret)
    {
        var secrets = Substitute.For<ISecretsModule>();
        secrets.GetValueAsync(Arg.Any<SecretKey>(), Arg.Any<CancellationToken>()).Returns(stored);

        return secrets;
    }

    private Task<WebhookOutcome> AcceptAsync(
        IntegrationDbContext db,
        IBackgroundJobClient hangfire,
        byte[] body,
        string? signature,
        string? timestamp = null,
        ISecretsModule? secrets = null,
        Guid? connectionId = null)
        => TelephonyWebhookEndpoints.AcceptAsync(
            connectionId ?? _connection,
            body,
            timestamp ?? Unix(),
            signature,
            db,
            TelephonyTestHarness.Resolver(db, new FakeAdapter()),
            TelephonyTestHarness.Payloads(),
            secrets ?? Secrets(),
            hangfire,
            TelephonyTestHarness.Clock,
            new IntegrationWebhookOptions(),
            NullLogger<TelephonyWebhookTests>.Instance,
            CancellationToken.None);

    private static IReadOnlyList<Job> Jobs(IBackgroundJobClient hangfire)
        => hangfire.ReceivedCalls()
            .Select(c => c.GetArguments()[0])
            .OfType<Job>()
            .Where(j => j.Type == typeof(ProcessTelephonyWebhookJob))
            .ToList();
}
