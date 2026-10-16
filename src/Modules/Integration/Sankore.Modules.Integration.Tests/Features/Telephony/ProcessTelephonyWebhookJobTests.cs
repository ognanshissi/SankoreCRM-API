namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Features.Telephony.Webhook;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Modules.Integration.Tests.TestSupport;
using Xunit;

/// <summary>
/// Turning a received event into a journal entry (TEL-B-07 → TEL-B-08).
///
/// <para>
/// The property every case here protects is that the body is NOT a data source: the event says
/// something happened and the provider's API says what. A signature-valid sender must not be able
/// to describe a call into existence with a status and numbers of its choosing.
/// </para>
/// </summary>
public sealed class ProcessTelephonyWebhookJobTests : IDisposable
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;
    private readonly TelephonyScreenPop _screenPop;
    private readonly TelephonyTestHarness.RecordingNotifier _pushes;

    public ProcessTelephonyWebhookJobTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        (_screenPop, _pushes) = TelephonyTestHarness.ScreenPop();

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task The_call_is_journalled_from_the_providers_own_reading()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1");

        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();
        var call = db.TelCalls.Single();

        // The body said "ringing". The API says the call completed, with its own durations — and
        // the API is what is written.
        call.ExternalCallId.Should().Be("fake-call-1");
        call.Status.Should().Be(TelCallStatus.Completed);
        call.TalkSeconds.Should().Be(90);
        call.Direction.Should().Be(TelCallDirection.Inbound);
    }

    [Fact]
    public async Task The_event_and_the_call_are_both_committed()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1");

        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();

        db.TelCalls.Should().HaveCount(1);
        db.TelWebhookEvents.Single().ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_event_already_processed_is_skipped()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1");

        await RunAsync(stored, new FakeAdapter());
        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();

        // Hangfire retries at least once, and the dedup index guards the RECEIPT rather than the
        // processing. This is the second guard, and it is the cheap one.
        db.TelCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_call_the_provider_does_not_know_is_finished_rather_than_retried()
    {
        var stored = await ReceiveAsync("ringing:unknown-call");

        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();

        db.TelCalls.Should().BeEmpty();
        db.TelWebhookEvents.Single().Outcome.Should().StartWith("FAILED:");
    }

    [Fact]
    public async Task A_retryable_failure_throws_so_hangfire_tries_again()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1");

        var adapter = new FakeAdapter
        {
            ForcedOutcome = IntegrationResult.Transient(IntegrationErrors.Timeout),
        };

        // The whole value of this path is the authoritative re-read. Giving up on a timeout would
        // leave the call out of the journal for good.
        var act = async () => await RunAsync(stored, adapter);

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var db = _factory.CreateContext();
        db.TelWebhookEvents.Single().ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Telephony_switched_off_between_receipt_and_processing_is_recorded_on_the_row()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1");

        await using (var seed = _factory.CreateContext())
        {
            var connection = await seed.Connections.AsTracking().SingleAsync(CancellationToken.None);
            connection.Deactivate(TelephonyTestHarness.Actor, TelephonyTestHarness.Clock);
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();

        // Visible rather than silently dropped: an operator has to be able to see events arriving
        // for a connection nobody is listening to any more.
        db.TelWebhookEvents.Single().Outcome
            .Should().Be($"SKIPPED:{TelephonyErrors.Disabled}");
        db.TelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_whose_payload_will_not_open_is_finished_and_not_retried()
    {
        var stored = await ReceiveAsync("ringing:fake-call-1", corruptPayload: true);

        await RunAsync(stored, new FakeAdapter());

        await using var db = _factory.CreateContext();

        // A key problem cannot change on a second attempt, and the row keeps saying so.
        db.TelWebhookEvents.Single().Outcome.Should().Be("SKIPPED:NO_CALL_ID");
        db.TelCalls.Should().BeEmpty();
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<TelWebhookEvent> ReceiveAsync(string body, bool corruptPayload = false)
    {
        await using var db = _factory.CreateContext();

        var payload = corruptPayload
            ? Encoding.UTF8.GetBytes("not an envelope this encryptor will ever open")
            : TelephonyTestHarness.Payloads().Protect(Encoding.UTF8.GetBytes(body));

        var stored = TelWebhookEvent.Receive(
            _tenant, _connection, $"dedup|{body}", "ringing", payload, TelephonyTestHarness.Now);

        db.TelWebhookEvents.Add(stored);
        await db.SaveChangesAsync(CancellationToken.None);

        return stored;
    }

    private async Task RunAsync(TelWebhookEvent stored, FakeAdapter adapter)
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

        await ProcessTelephonyWebhookJob.RunAsync(
            db,
            TelephonyTestHarness.Access(db, adapter),
            TelephonyTestHarness.Writer(db, customers, leads),
            TelephonyTestHarness.Payloads(),
            _screenPop,
            TelephonyTestHarness.Clock,
            NullLogger<ProcessTelephonyWebhookJob>.Instance,
            _tenant,
            _connection,
            stored.Id,
            stored.ReceivedAt,
            CancellationToken.None);
    }
}
