namespace Sankore.Modules.Integration.Tests.Features.Webhooks;

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Webhooks;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// Outbound webhooks, lot 3: an event becomes one delivery per subscription that wants it, each
/// delivery is sent, retried on its schedule, given up after its last attempt, and a partner dead
/// for a day gets its subscription switched off — with somebody told.
/// </summary>
public sealed class WebhookDeliveryTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000c");
    private static readonly Guid Admin = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    private readonly TestIntegrationDbContextFactory _factory = new(TenantId);
    private readonly InMemorySecrets _vault = new();
    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero));
    private readonly IBackgroundJobClient _jobs = Substitute.For<IBackgroundJobClient>();
    private readonly INotificationsModule _notifications = Substitute.For<INotificationsModule>();

    // ── Fan-out ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_event_becomes_one_delivery_per_active_subscription_that_wants_it_and_only_once()
    {
        await using var db = _factory.CreateContext();
        var wants = await SubscribeAsync(db, ["lead.status_changed"]);
        await SubscribeAsync(db, ["lead.created"]);
        var off = await SubscribeAsync(db, ["lead.status_changed"]);
        off.SetActive(false, Admin, _clock.GetUtcNow());
        await db.SaveChangesAsync();

        var leadId = Guid.NewGuid();
        var @event = new LeadStatusChangedEvent(TenantId, leadId, "New", "Open");

        var created = await Dispatcher(db, new RecordingPartner(HttpStatusCode.OK)).FanOutAsync(@event, default);
        var again = await Dispatcher(db, new RecordingPartner(HttpStatusCode.OK)).FanOutAsync(@event, default);

        created.Should().ContainSingle();
        again.Should().BeEmpty("a redelivered event finds its row and creates nothing");

        var delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.SubscriptionId.Should().Be(wants.Id);
        delivery.EventId.Should().Be(@event.EventId);
        delivery.LeadId.Should().Be(leadId);
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);

        using var body = JsonDocument.Parse(delivery.Body);
        body.RootElement.GetProperty("id").GetGuid().Should().Be(@event.EventId);
        body.RootElement.GetProperty("type").GetString().Should().Be("lead.status_changed");
        body.RootElement.TryGetProperty("test", out _).Should().BeFalse();

        _jobs.Received(1).Create(Arg.Is<Job>(j => j.Type == typeof(WebhookDeliveryJob)), Arg.Any<EnqueuedState>());
    }

    [Fact]
    public async Task A_test_tenants_signing_secret_says_so()
    {
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(TenantId, "Caisse test", "test.sankore.local", true, false, null, null, IsTest: true));

        (await new WebhookSecrets(_vault, _clock, tenants).CreateAsync(TenantId, Guid.NewGuid(), default))
            .Should().StartWith(TestModeKeys.TestWebhookSecret);
        (await new WebhookSecrets(_vault, _clock).CreateAsync(TenantId, Guid.NewGuid(), default))
            .Should().StartWith(TestModeKeys.LiveWebhookSecret).And.NotStartWith(TestModeKeys.TestWebhookSecret);
    }

    [Fact]
    public async Task A_test_tenants_events_are_marked_test_like_a_sample()
    {
        await using var db = _factory.CreateContext();
        await SubscribeAsync(db, ["lead.status_changed"]);
        var tenants = Substitute.For<ITenantStore>();
        tenants.GetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new TenantInfo(TenantId, "Caisse test", "test.sankore.local", true, false, null, null, IsTest: true));

        await Dispatcher(db, new RecordingPartner(HttpStatusCode.OK), tenants)
            .FanOutAsync(new LeadStatusChangedEvent(TenantId, Guid.NewGuid(), "New", "Open"), default);

        // Still delivered — a partner wires its test endpoint to a test tenant on purpose — but it
        // can tell, as from Stripe's `livemode: false`.
        using var body = JsonDocument.Parse((await db.WebhookDeliveries.SingleAsync()).Body);
        body.RootElement.GetProperty("test").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_event_outside_the_catalogue_or_nobody_listening_creates_nothing()
    {
        await using var db = _factory.CreateContext();

        (await Dispatcher(db, new RecordingPartner(HttpStatusCode.OK))
                .FanOutAsync(new LeadCreatedEvent(TenantId, Guid.NewGuid(), "New", "Web", null, null, null, null), default))
            .Should().BeEmpty();
        (await db.WebhookDeliveries.CountAsync()).Should().Be(0);
    }

    // ── Sending, retrying, giving up ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_delivery_the_partner_accepts_is_marked_sent_with_its_own_id_in_the_header()
    {
        await using var db = _factory.CreateContext();
        var subscription = await SubscribeAsync(db, ["lead.created"]);
        var partner = new RecordingPartner(HttpStatusCode.Accepted);
        var deliveryId = (await FanOutLeadCreatedAsync(db, partner)).Single();

        await Dispatcher(db, partner).DeliverAsync(deliveryId, default);

        var delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Succeeded);
        delivery.Attempts.Should().Be(1);
        delivery.LastStatusCode.Should().Be(202);
        delivery.DeliveredAt.Should().Be(_clock.GetUtcNow());
        partner.Requests.Single().Headers[WebhookPayload.DeliveryHeader].Should().Be(deliveryId.ToString());
        partner.Requests.Single().Body.Should().Be(delivery.Body);

        (await db.WebhookSubscriptions.SingleAsync(s => s.Id == subscription.Id)).LastSuccessAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task A_failure_is_retried_on_its_schedule_and_given_up_after_the_last_attempt()
    {
        await using var db = _factory.CreateContext();
        await SubscribeAsync(db, ["lead.created"]);
        var partner = new RecordingPartner(HttpStatusCode.ServiceUnavailable);
        var deliveryId = (await FanOutLeadCreatedAsync(db, partner)).Single();

        await Dispatcher(db, partner).DeliverAsync(deliveryId, default);

        var delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.LastFailureCode.Should().Be("HTTP_503");
        delivery.NextAttemptAt.Should().Be(_clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        _jobs.Received(1).Create(Arg.Is<Job>(j => j.Type == typeof(WebhookDeliveryJob)), Arg.Any<ScheduledState>());

        // Not due yet: a run now sends nothing.
        await Dispatcher(db, partner).DeliverAsync(deliveryId, default);
        partner.Requests.Should().HaveCount(1);

        foreach (var wait in WebhookDelivery.RetryDelays)
        {
            _clock.Advance(wait);
            await Dispatcher(db, partner).DeliverAsync(deliveryId, default);
        }

        delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);
        delivery.Attempts.Should().Be(WebhookDelivery.MaxAttempts);
        delivery.NextAttemptAt.Should().BeNull();
        partner.Requests.Should().HaveCount(WebhookDelivery.MaxAttempts);
    }

    [Fact]
    public async Task A_delivery_whose_subscription_was_switched_off_is_cancelled_unsent()
    {
        await using var db = _factory.CreateContext();
        var subscription = await SubscribeAsync(db, ["lead.created"]);
        var partner = new RecordingPartner(HttpStatusCode.OK);
        var deliveryId = (await FanOutLeadCreatedAsync(db, partner)).Single();

        subscription.SetActive(false, Admin, _clock.GetUtcNow());
        await db.SaveChangesAsync();
        await Dispatcher(db, partner).DeliverAsync(deliveryId, default);

        var delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Cancelled);
        delivery.LastFailureCode.Should().Be("SUBSCRIPTION_DISABLED");
        partner.Requests.Should().BeEmpty();
    }

    // ── Switching a dead partner off ────────────────────────────────────────────────────────

    [Fact]
    public void Twenty_failures_in_a_row_switch_off_only_after_a_day_without_success()
    {
        var subscription = WebhookSubscription.Create(TenantId, "P", "https://p.example.com/", null, ["lead.created"], Admin, _clock.GetUtcNow()).Value!;
        subscription.RecordSuccess(_clock.GetUtcNow());

        // Twenty failures within the hour after a success: a short outage, not a dead partner.
        for (var i = 0; i < 25; i++)
            subscription.RecordFailure("HTTP_503", _clock.GetUtcNow().AddMinutes(i)).Should().BeFalse();
        subscription.IsActive.Should().BeTrue();

        var dayLater = _clock.GetUtcNow() + WebhookSubscription.AutoDisableQuietPeriod + TimeSpan.FromMinutes(1);
        subscription.RecordFailure("HTTP_503", dayLater).Should().BeTrue();
        subscription.IsActive.Should().BeFalse();
        subscription.DisabledAt.Should().Be(dayLater);

        subscription.RecordFailure("HTTP_503", dayLater.AddMinutes(1)).Should().BeFalse("it is told once");
    }

    [Fact]
    public async Task Switching_off_tells_who_configured_it()
    {
        await using var db = _factory.CreateContext();
        var subscription = await SubscribeAsync(db, ["lead.created"]);
        var partner = new RecordingPartner(HttpStatusCode.InternalServerError);
        typeof(WebhookSubscription).GetProperty(nameof(WebhookSubscription.ConsecutiveFailures))!
            .SetValue(subscription, WebhookSubscription.AutoDisableAfterFailures - 1);
        await db.SaveChangesAsync();

        _clock.Advance(TimeSpan.FromDays(2));
        var deliveryId = (await FanOutLeadCreatedAsync(db, partner)).Single();
        await Dispatcher(db, partner).DeliverAsync(deliveryId, default);

        (await db.WebhookSubscriptions.SingleAsync()).IsActive.Should().BeFalse();
        await _notifications.Received(1).NotifyInAppAsync(
            Arg.Is<InAppNotificationRequest>(r => r.UserId == Admin && r.Category == "integration.webhook.disabled"),
            Arg.Any<CancellationToken>());
    }

    // ── Replay and erasure ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_delivery_can_be_sent_again_with_the_same_event_id()
    {
        await using var db = _factory.CreateContext();
        var subscription = await SubscribeAsync(db, ["lead.created"]);
        var deliveryId = (await FanOutLeadCreatedAsync(db, new RecordingPartner(HttpStatusCode.OK))).Single();
        var delivery = await db.WebhookDeliveries.AsTracking().SingleAsync();
        delivery.RecordAttempt(500, false, 10, "HTTP_500", _clock.GetUtcNow());
        foreach (var _ in WebhookDelivery.RetryDelays) delivery.RecordAttempt(500, false, 10, "HTTP_500", _clock.GetUtcNow());
        await db.SaveChangesAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Failed);

        var replayed = await new ReplayWebhookDeliveryHandler(db, Commands.CommandsTestHarness.User(TenantId), _jobs, _clock)
            .Handle(new ReplayWebhookDeliveryCommand(subscription.Id, deliveryId), default);

        replayed.IsSuccess.Should().BeTrue();
        delivery = await db.WebhookDeliveries.SingleAsync();
        delivery.Status.Should().Be(WebhookDeliveryStatus.Pending);
        delivery.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Erasing_a_lead_blanks_what_was_kept_about_them_and_keeps_the_erasure_notice()
    {
        await using var db = _factory.CreateContext();
        await SubscribeAsync(db, ["lead.status_changed", "lead.erased"]);
        var leadId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var dispatcher = Dispatcher(db, new RecordingPartner(HttpStatusCode.OK));

        await dispatcher.FanOutAsync(new LeadStatusChangedEvent(TenantId, leadId, "New", "Open"), default);
        await dispatcher.FanOutAsync(new LeadStatusChangedEvent(TenantId, other, "New", "Open"), default);

        await dispatcher.EraseSubjectAsync(leadId, null, default);
        await dispatcher.FanOutAsync(new LeadErasedEvent(TenantId, leadId, _clock.GetUtcNow()), default);

        var rows = await db.WebhookDeliveries.ToListAsync();
        rows.Single(d => d.LeadId == leadId && d.EventCode == "lead.status_changed").Should()
            .Match<WebhookDelivery>(d => d.Body == "" && d.Status == WebhookDeliveryStatus.Cancelled);
        rows.Single(d => d.LeadId == leadId && d.EventCode == "lead.erased").Status.Should().Be(WebhookDeliveryStatus.Pending);
        rows.Single(d => d.LeadId == other).Body.Should().NotBeEmpty();
    }

    // ── The consumer covers the catalogue ───────────────────────────────────────────────────

    [Fact]
    public void Every_catalogue_event_is_consumed_and_carries_its_tenant()
    {
        var consumed = typeof(WebhookFanOutConsumer).GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>))
            .Select(i => i.GetGenericArguments()[0]);

        consumed.Should().BeEquivalentTo(WebhookEventCatalog.All.Select(d => d.EventType));

        foreach (var definition in WebhookEventCatalog.All)
            WebhookDispatcher.TenantOf(definition.Sample(TenantId)).Should().Be(TenantId, definition.Code);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task<WebhookSubscription> SubscribeAsync(IntegrationDbContext db, IReadOnlyList<string> events)
    {
        var subscription = WebhookSubscription.Create(TenantId, "Partenaire", "https://partner.example.com/hooks", null, events, Admin, _clock.GetUtcNow()).Value!;
        db.WebhookSubscriptions.Add(subscription);
        await db.SaveChangesAsync();
        await new WebhookSecrets(_vault, _clock).CreateAsync(TenantId, subscription.Id, default);
        return subscription;
    }

    private Task<IReadOnlyList<Guid>> FanOutLeadCreatedAsync(IntegrationDbContext db, RecordingPartner partner)
        => Dispatcher(db, partner).FanOutAsync(new LeadCreatedEvent(TenantId, Guid.NewGuid(), "New", "Web", null, null, null, null), default);

    private WebhookDispatcher Dispatcher(IntegrationDbContext db, RecordingPartner partner, ITenantStore? tenants = null)
        => new(db, FixedTenantModules.All, new WebhookSecrets(_vault, _clock),
            new WebhookSender(new SingleClientFactory(new HttpClient(partner)), _clock, NullLogger<WebhookSender>.Instance),
            _jobs, _notifications, _clock, NullLogger<WebhookDispatcher>.Instance, tenants);

    public void Dispose() => _factory.Dispose();
}
