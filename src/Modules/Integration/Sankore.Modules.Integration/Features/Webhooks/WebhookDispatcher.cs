namespace Sankore.Modules.Integration.Features.Webhooks;

using Hangfire;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Retention;
using Sankore.Shared.Infrastructure.Workflow;
using Sankore.Shared.Kernel;

/// <summary>
/// Outbound webhooks, lot 3: an integration event of the catalogue becomes one
/// <see cref="WebhookDelivery"/> per active subscription that wants it (<see cref="FanOutAsync"/>),
/// then each delivery is sent, retried on <see cref="WebhookDelivery.RetryDelays"/> and recorded
/// (<see cref="DeliverAsync"/>). Scoped, tenant-bound: the consumer and the job open the scope.
///
/// <para>
/// <b>At least once.</b> A partner may receive the same event twice — a redelivered message after
/// a crash between the send and the save, a manual replay — and must deduplicate on the envelope
/// <c>id</c>. Ordering is not guaranteed either: a retried event can arrive after a later one,
/// which is why every payload carries <c>occurredAt</c>.
/// </para>
/// </summary>
internal sealed class WebhookDispatcher(
    IntegrationDbContext db,
    ITenantModules modules,
    WebhookSecrets secrets,
    WebhookSender sender,
    IBackgroundJobClient jobs,
    INotificationsModule notifications,
    TimeProvider clock,
    ILogger<WebhookDispatcher> logger)
{
    /// <returns>The deliveries created — none for an event outside the catalogue, or already fanned out.</returns>
    public async Task<IReadOnlyList<Guid>> FanOutAsync(IIntegrationEvent @event, CancellationToken ct)
    {
        var definition = WebhookEventCatalog.For(@event);
        if (definition is null) return [];

        var tenantId = TenantOf(@event);
        if (definition.RequiredModule is not null && !await modules.IsEnabledAsync(tenantId, definition.RequiredModule, ct))
            return [];

        var subscriptions = await db.WebhookSubscriptions
            .Where(s => s.IsActive && s.Events.Contains(definition.Code))
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (subscriptions.Count == 0) return [];

        // Read first, and the unique index behind it: a broker redelivery finds its rows and adds none.
        var already = await db.WebhookDeliveries
            .Where(d => d.EventId == @event.EventId && subscriptions.Contains(d.SubscriptionId))
            .Select(d => d.SubscriptionId)
            .ToListAsync(ct);

        var data = definition.Data(@event);
        var body = WebhookPayload.Body(definition, @event);
        var now = clock.GetUtcNow();

        var created = subscriptions.Except(already)
            .Select(id => WebhookDelivery.Create(
                tenantId, id, @event.EventId, definition.Code, IdOf(data, "leadId"), IdOf(data, "clientId"), body, now))
            .ToList();
        if (created.Count == 0) return [];

        db.WebhookDeliveries.AddRange(created);
        await db.SaveChangesAsync(ct);

        // After the save: a job enqueued for a row that never committed would find nothing. A lost
        // enqueue is picked up by WebhookDeliverySweepJob.
        foreach (var delivery in created)
            jobs.Enqueue<WebhookDeliveryJob>(j => j.ExecuteAsync(tenantId, delivery.Id));

        return created.Select(d => d.Id).ToList();
    }

    /// <summary>One attempt, if the delivery is due. Schedules the next one when it failed.</summary>
    public async Task DeliverAsync(Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.WebhookDeliveries.AsTracking().FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null || !delivery.TryClaim(clock.GetUtcNow())) return;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another run claimed it in the same instant; it sends, this one does not.
            return;
        }

        var subscription = await db.WebhookSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == delivery.SubscriptionId, ct);
        if (subscription is null || !subscription.IsActive)
        {
            delivery.Cancel(subscription is null ? "SUBSCRIPTION_DELETED" : "SUBSCRIPTION_DISABLED");
            await db.SaveChangesAsync(ct);
            return;
        }

        var keys = await secrets.SigningKeysAsync(delivery.TenantId, subscription.Id, ct);
        var attempt = keys.Count == 0
            ? new WebhookAttempt(null, false, 0, "NO_SECRET")
            : await sender.SendAsync(subscription.Url, delivery.EventCode, delivery.Id, delivery.Body, keys, ct);

        var now = clock.GetUtcNow();
        var retry = delivery.RecordAttempt(attempt.StatusCode, attempt.Succeeded, attempt.DurationMs, attempt.FailureCode, now);

        var switchedOff = false;
        if (attempt.Succeeded) subscription.RecordSuccess(now);
        else switchedOff = subscription.RecordFailure(attempt.FailureCode, now);

        await db.SaveChangesAsync(ct);

        if (retry)
        {
            var tenantId = delivery.TenantId;
            jobs.Schedule<WebhookDeliveryJob>(j => j.ExecuteAsync(tenantId, deliveryId), delivery.NextAttemptAt!.Value - now);
        }

        if (switchedOff) await TellSwitchedOffAsync(subscription, ct);
    }

    /// <summary>
    /// The person who last configured it, and its creator if somebody else: they are who can say
    /// whether the partner moved, and nobody else would notice a webhook that stopped.
    /// </summary>
    private async Task TellSwitchedOffAsync(WebhookSubscription subscription, CancellationToken ct)
    {
        foreach (var userId in new[] { subscription.UpdatedBy, subscription.CreatedBy }.Distinct())
        {
            try
            {
                await notifications.NotifyInAppAsync(new InAppNotificationRequest(
                    subscription.TenantId, userId, "integration.webhook.disabled",
                    "Un webhook a été désactivé",
                    $"« {subscription.Name} » échoue depuis plus de 24 h ({subscription.LastFailureCode}). Vérifiez l’adresse puis réactivez-le.",
                    $"/settings/webhooks/{subscription.Id}",
                    $"webhook.disabled:{subscription.Id}:{subscription.DisabledAt?.ToUnixTimeSeconds()}:{userId}"), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Webhook {SubscriptionId} was switched off but {UserId} could not be told.", subscription.Id, userId);
            }
        }
    }

    /// <summary>
    /// A lead or a client was erased: what is still owed about them is not sent, and every stored
    /// body that names them is blanked. The erasure event itself stays — it is what tells the
    /// partner to delete its own copy.
    /// </summary>
    public async Task EraseSubjectAsync(Guid? leadId, Guid? clientId, CancellationToken ct)
    {
        var deliveries = await db.WebhookDeliveries.AsTracking()
            .Where(d => (leadId != null && d.LeadId == leadId) || (clientId != null && d.ClientId == clientId))
            .Where(d => d.EventCode != "lead.erased" && d.EventCode != "client.anonymized")
            .ToListAsync(ct);

        foreach (var delivery in deliveries) delivery.Erase();
        if (deliveries.Count > 0) await db.SaveChangesAsync(ct);
    }

    internal static Guid TenantOf(IIntegrationEvent @event)
        => (Guid)@event.GetType().GetProperty("TenantId")!.GetValue(@event)!;

    private static Guid? IdOf(IReadOnlyDictionary<string, object?> data, string key)
        => data.TryGetValue(key, out var value) && value is Guid id && id != Guid.Empty ? id : null;
}

/// <summary>Sends one delivery. Hangfire retries nothing itself: the delivery holds its own schedule.</summary>
public sealed class WebhookDeliveryJob(IServiceScopeFactory scopeFactory)
{
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid tenantId, Guid deliveryId)
    {
        // Before the scope: ITenantContext is built from this ambient context.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WebhookDispatcher>().DeliverAsync(deliveryId, CancellationToken.None);
    }
}

/// <summary>
/// The safety net, every five minutes across tenants: a delivery whose scheduled run was lost — a
/// Hangfire row deleted, an enqueue that failed after the fan-out committed — is overdue, and
/// enqueued again. Overdue by more than a claim lease, so a run in progress is never doubled.
/// </summary>
public sealed class WebhookDeliverySweepJob(IServiceScopeFactory scopeFactory, IBackgroundJobClient jobs, TimeProvider clock)
{
    private const int BatchSize = 500;

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var overdue = clock.GetUtcNow() - WebhookDelivery.ClaimLease;

        var due = await db.WebhookDeliveries
            .IgnoreQueryFilters()
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt < overdue)
            .OrderBy(d => d.NextAttemptAt)
            .Take(BatchSize)
            .Select(d => new { d.TenantId, d.Id })
            .ToListAsync();

        foreach (var d in due)
            jobs.Enqueue<WebhookDeliveryJob>(j => j.ExecuteAsync(d.TenantId, d.Id));
    }
}

/// <summary>
/// Every event of the catalogue, one interface each — not a generic consumer: the endpoint names
/// stay readable, and a test fails when a catalogue entry has no interface here. Two consumers
/// derive from it, each with its own queue: the webhook deliveries and the workflow bridge.
/// </summary>
public abstract class CatalogueEventConsumer :
    IConsumer<LeadCreatedEvent>, IConsumer<LeadAssignedEvent>, IConsumer<LeadStatusChangedEvent>,
    IConsumer<LeadStageChangedEvent>, IConsumer<LeadQualifiedEvent>, IConsumer<LeadConvertedToClientEvent>,
    IConsumer<LeadClosedEvent>, IConsumer<LeadReopenedEvent>, IConsumer<LeadMergedEvent>,
    IConsumer<LeadConsentWithdrawnEvent>, IConsumer<LeadErasedEvent>,
    IConsumer<ProjectCreatedEvent>, IConsumer<ProjectStageChangedEvent>, IConsumer<ProjectClosedEvent>,
    IConsumer<ClientCreatedEvent>, IConsumer<ClientUpdatedEvent>, IConsumer<ClientSensitiveDataChangedEvent>,
    IConsumer<ClientActivatedEvent>, IConsumer<ClientSuspendedEvent>, IConsumer<ClientArchivedEvent>,
    IConsumer<ClientTransferredEvent>, IConsumer<ClientsMergedEvent>, IConsumer<ClientSegmentChangedEvent>,
    IConsumer<KycValidatedEvent>, IConsumer<KycRejectedEvent>, IConsumer<ClientAnonymizedEvent>
{
    protected abstract Task HandleAsync(IIntegrationEvent @event, ConsumeContext context);

    public Task Consume(ConsumeContext<LeadCreatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadAssignedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadStatusChangedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadStageChangedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadQualifiedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadConvertedToClientEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadClosedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadReopenedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadMergedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadConsentWithdrawnEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<LeadErasedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ProjectCreatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ProjectStageChangedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ProjectClosedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientCreatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientUpdatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientSensitiveDataChangedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientActivatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientSuspendedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientArchivedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientTransferredEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientsMergedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientSegmentChangedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<KycValidatedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<KycRejectedEvent> c) => HandleAsync(c.Message, c);
    public Task Consume(ConsumeContext<ClientAnonymizedEvent> c) => HandleAsync(c.Message, c);
}

/// <summary>Every catalogue event, turned into one delivery per subscription that wants it.</summary>
public sealed class WebhookFanOutConsumer(IServiceScopeFactory scopeFactory) : CatalogueEventConsumer
{
    protected override async Task HandleAsync(IIntegrationEvent @event, ConsumeContext context)
    {
        var ct = context.CancellationToken;

        // Erasure first, then the announcement: the partner is told, nothing older about the
        // person goes out.
        switch (@event)
        {
            case LeadErasedEvent erased:
                await Within(erased.TenantId, d => d.EraseSubjectAsync(erased.LeadId, null, ct));
                break;
            case ClientAnonymizedEvent anonymized:
                await Within(anonymized.TenantId, d => d.EraseSubjectAsync(null, anonymized.ClientId, ct));
                break;
        }

        await Within(WebhookDispatcher.TenantOf(@event), d => d.FanOutAsync(@event, ct));
    }

    private async Task Within(Guid tenantId, Func<WebhookDispatcher, Task> work)
    {
        if (tenantId == Guid.Empty) return;

        // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await work(scope.ServiceProvider.GetRequiredService<WebhookDispatcher>());
    }
}

/// <summary>
/// Every catalogue event, offered to the Workflow module as an <c>EntityEvent</c> trigger named by
/// its code (<c>lead.status_changed</c>…), on the entity the code is about. Before this bridge the
/// CRM published a single trigger event — <c>LEAD_CAPTURED</c> — and the editor's event name was a
/// free field nothing else ever matched.
///
/// <para>
/// The context is the catalogue's payload, the same one webhooks carry: ids, enum names, amounts
/// and dates, no personal value — trigger conditions read <c>status</c>, <c>stage</c>,
/// <c>outcome</c>… Erasure notices start nothing (<see cref="PlatformEventDefinition.StartsWorkflows"/>).
/// </para>
/// </summary>
public sealed class WorkflowEventBridgeConsumer : CatalogueEventConsumer
{
    protected override Task HandleAsync(IIntegrationEvent @event, ConsumeContext context)
    {
        var signal = ToSignal(@event);
        return signal is null ? Task.CompletedTask : context.Publish(signal, context.CancellationToken);
    }

    internal static WorkflowTriggerSignal? ToSignal(IIntegrationEvent @event)
    {
        var definition = WebhookEventCatalog.For(@event);
        if (definition is null) return null;

        var platform = PlatformEvents.Get(definition.Code);
        if (!platform.StartsWorkflows) return null;

        var data = definition.Data(@event);
        if (!data.TryGetValue(platform.EntityIdKey, out var id) || id is not Guid entityId || entityId == Guid.Empty)
            return null;

        var context = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in data)
        {
            // The condition evaluator compares strings, numbers and booleans.
            object? plain = value switch
            {
                null => null,
                Guid g => g.ToString(),
                decimal d => (double)d,
                int i => (double)i,
                DateTimeOffset at => at.ToString("O"),
                string[] names => string.Join(",", names),
                _ => value,
            };
            if (plain is not null) context[key] = plain;
        }

        return new WorkflowTriggerSignal(
            WebhookDispatcher.TenantOf(@event), platform.WorkflowEntityType, entityId, platform.Code, context);
    }
}

/// <summary>Delivery log rows past their use: sent, given up or cancelled more than 30 days ago.</summary>
internal sealed class WebhookDeliveryPurge(IntegrationDbContext db) : ITechnicalPurge
{
    public string Name => "integration.whk_delivery";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ProcessedMessages;
        return db.WebhookDeliveries.IgnoreQueryFilters()
            .Where(d => d.Status != WebhookDeliveryStatus.Pending && d.CreatedAt < before)
            .ExecuteDeleteAsync(ct);
    }
}
