namespace Sankore.Modules.Notifications.Infrastructure.Sms;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Notifications.Domain;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Polls <c>sms_outbox_messages</c> and hands pending messages to the tenant's provider.
///
/// <para>
/// The same shape as <c>EmailOutboxProcessor</c> — singleton, a scope per message, claim before
/// send, quota at the one moment that consumes an allowance — because an operator who has
/// debugged one should recognise the other. Four things differ, and each is about SMS costing
/// money per message:
/// </para>
///
/// <list type="number">
/// <item><b>An unconfigured channel dead-letters immediately.</b> M12 answers
///   <c>CHANNEL_NOT_CONFIGURED</c> rather than granting an allowance, so the message never
///   reaches a sender that would fail on a missing credential and report a network error.</item>
/// <item><b>A missing template dead-letters too.</b> The email renderer falls back to the raw
///   payload and lets the message go; doing that here would spend a tenant's money to send a
///   customer a JSON fragment.</item>
/// <item><b>The destination is REDACTED on every terminal state.</b> The number is in the row
///   because the processor needs it, and has no reason to stay once the message can no longer be
///   sent.</item>
/// <item><b>The segment count is recorded.</b> It is what the tenant is billed for, and a
///   template that drifted past 160 characters is otherwise invisible until an invoice.</item>
/// </list>
/// </summary>
internal sealed class SmsOutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<SmsOutboxProcessorOptions> options,
    ILogger<SmsOutboxProcessor> logger)
    : BackgroundService
{
    private readonly SmsOutboxProcessorOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "SmsOutboxProcessor started — interval={Interval}s batch={Batch} maxAttempts={Max}",
            _opts.PollingIntervalSeconds, _opts.BatchSize, _opts.MaxAttempts);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_opts.PollingIntervalSeconds), stoppingToken);
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SmsOutboxProcessor unhandled error — will retry next tick");
            }
        }

        logger.LogInformation("SmsOutboxProcessor stopped");
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        // Claims that were never resolved — the process died, or the provider hung after the
        // claim committed. Without this cutoff they are invisible to the query below for ever.
        var stuckBefore = DateTimeOffset.UtcNow.AddMinutes(-_opts.StuckSendingTimeoutMinutes);

        List<Guid> ids;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();

            ids = await db.SmsOutboxMessages
                .IgnoreQueryFilters()
                .Where(m =>
                    (m.Status == SmsOutboxStatus.Pending
                     || m.Status == SmsOutboxStatus.Failed
                     || (m.Status == SmsOutboxStatus.Sending
                         && m.LastAttemptAt != null
                         && m.LastAttemptAt < stuckBefore))
                    && m.AttemptCount < _opts.MaxAttempts)
                .OrderBy(m => m.CreatedAt)
                .Take(_opts.BatchSize)
                .Select(m => m.Id)
                .ToListAsync(ct);
        }

        if (ids.Count == 0) return;

        logger.LogDebug("SmsOutboxProcessor picked up {Count} message(s)", ids.Count);

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await ProcessOneAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SmsOutboxProcessor failed to process message {MessageId} — skipping", id);
            }
        }
    }

    private async Task ProcessOneAsync(Guid messageId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<ISmsProviderResolver>();
        var renderer = scope.ServiceProvider.GetRequiredService<ISmsTemplateRenderer>();
        var sender = scope.ServiceProvider.GetRequiredService<ISmsSender>();
        var admin = scope.ServiceProvider.GetRequiredService<IAdministrationModule>();

        var message = await db.SmsOutboxMessages
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null
            || message.Status is SmsOutboxStatus.Sent or SmsOutboxStatus.DeadLettered)
        {
            return;
        }

        // The destination, kept for the send and for the masked form every terminal path writes.
        var phone = message.RecipientPhone;
        var masked = SensitiveValueMasker.MaskPhone(phone) ?? "••";

        // Claim it — stops two instances sending the same message, which on this channel is a
        // customer receiving two codes and a tenant paying for both.
        message.MarkSending();
        await db.SaveChangesAsync(ct);

        // ── Test mode ───────────────────────────────────────────────────────
        // Before the allowance: a text a test tenant may not send must cost it nothing.
        if (await admin.IsPhoneBlockedInTestAsync(message.TenantId, phone, ct))
        {
            message.MarkRefused($"{EmailOutboxMessage.TestModeBlocked}: tenant in test mode, the number is not one of its users'.");
            message.Redact(masked);

            db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
                message.TenantId, message.Id, SmsDeliveryEventType.Rejected, masked,
                "{\"source\":\"sms_outbox_processor\",\"reason\":\"test_mode_recipient\"}"));

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "SMS not sent — test tenant, number outside its team | MessageId={Id} Tenant={TenantId}",
                message.Id, message.TenantId);

            return;
        }

        // ── The allowance, and whether there is a channel at all ────────────
        // One gate, at the only moment that actually consumes anything. M12 answers
        // CHANNEL_NOT_CONFIGURED for a tenant with no provider, which is why this check comes
        // before the resolver: a message for an unconfigured tenant must not reach a sender that
        // would fail on a missing credential and look like a provider outage.
        var quota = await admin.TryConsumeSmsQuotaAsync(message.TenantId, ct);

        if (!quota.Granted)
        {
            var reason = quota.Reason ?? "SMS_REFUSED";

            message.MarkRefused(
                $"{reason}: {quota.UsedThisMonth} of {quota.Limit?.ToString() ?? "unlimited"} used this month.");

            message.Redact(masked);

            db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
                message.TenantId, message.Id, SmsDeliveryEventType.Rejected, masked,
                $"{{\"source\":\"sms_outbox_processor\",\"reason\":\"{reason}\"}}"));

            await db.SaveChangesAsync(ct);

            logger.LogWarning(
                "SMS refused | MessageId={Id} Tenant={TenantId} Reason={Reason}",
                message.Id, message.TenantId, reason);

            return;
        }

        try
        {
            var provider = await resolver.ResolveAsync(message.TenantId, ct);

            var rendered = await renderer.RenderAsync(
                message.TenantId, message.TemplateKey, message.Locale, message.TemplateDataJson, ct);

            if (!rendered.Found || string.IsNullOrWhiteSpace(rendered.Body))
            {
                // Dead-lettered rather than retried: a template does not appear on its own, and
                // retrying would bill the tenant twice for a message with nothing in it. See the
                // renderer for why it does not fabricate a body the way the email one does.
                message.MarkRefused($"NO_SMS_TEMPLATE: {message.TemplateKey} ({message.Locale})");
                message.Redact(masked);

                db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
                    message.TenantId, message.Id, SmsDeliveryEventType.Rejected, masked,
                    $"{{\"source\":\"sms_outbox_processor\",\"reason\":\"no_template\"," +
                    $"\"template\":\"{message.TemplateKey}\"}}"));

                await db.SaveChangesAsync(ct);

                logger.LogError(
                    "SMS not sent | MessageId={Id} no active template {Key} for locale {Locale}",
                    message.Id, message.TemplateKey, message.Locale);

                return;
            }

            var result = await sender.SendAsync(
                new SendSmsRequest(message.Id, message.TenantId, phone, rendered.Body),
                provider, ct);

            message.MarkSent(result.ProviderMessageId, rendered.SegmentCount);

            if (result.ProviderStatus is { } status)
                message.RecordProviderStatus(status, error: null);

            // Redacted on success too. The provider has the number now; this row does not need
            // it, and a delivery callback correlates on the provider's id rather than on it.
            message.Redact(masked);

            db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
                message.TenantId, message.Id, SmsDeliveryEventType.Accepted, masked,
                $"{{\"source\":\"sms_outbox_processor\",\"provider\":\"{provider.ProviderType}\"," +
                $"\"segments\":{rendered.SegmentCount}}}",
                result.ProviderStatus));

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "SMS accepted by provider | MessageId={Id} Tenant={TenantId} To={Masked} "
                + "Provider={Provider} Segments={Segments} ProviderId={ProviderId}",
                message.Id, message.TenantId, masked, provider.ProviderType,
                rendered.SegmentCount, result.ProviderMessageId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;

            logger.LogWarning(ex,
                "SMS send failed | MessageId={Id} Attempt={Attempt}",
                message.Id, message.AttemptCount + 1);

            message.MarkFailed(error, _opts.MaxAttempts);

            // Only once there is nothing left to try. Redacting on a retriable failure would
            // leave the next attempt with a masked number to send to.
            if (message.Status == SmsOutboxStatus.DeadLettered)
                message.Redact(masked);

            db.SmsDeliveryLogs.Add(SmsDeliveryLog.Record(
                message.TenantId, message.Id, SmsDeliveryEventType.Rejected, masked,
                $"{{\"source\":\"sms_outbox_processor\",\"error\":" +
                $"{System.Text.Json.JsonSerializer.Serialize(error)}}}"));

            await db.SaveChangesAsync(ct);
        }
    }
}
