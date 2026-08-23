namespace Sankore.Modules.Notifications.Infrastructure.Processor;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Notifications.Domain;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Modules.Notifications.Infrastructure.Providers;
using Sankore.Modules.Notifications.Infrastructure.Rendering;
using Sankore.Modules.Notifications.Infrastructure.Senders;

/// <summary>
/// Background service that polls the email_outbox_messages table and dispatches
/// Pending / Failed (retriable) messages via IEmailSender.
///
/// Design:
///   • Runs as a singleton; uses IServiceScopeFactory for scoped services.
///   • Each message is processed in its own scope+transaction — failures are isolated.
///   • Marks a message Sending before calling the provider (optimistic claim).
///   • On success: marks Sent + appends an EmailDeliveryLog (Delivered).
///   • On failure: calls MarkFailed (increments AttemptCount; DeadLetters at MaxAttempts)
///     + appends an EmailDeliveryLog (Rejected).
///   • Uses IgnoreQueryFilters() — processes messages for ALL tenants.
/// </summary>
internal sealed class EmailOutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOutboxProcessorOptions> options,
    ILogger<EmailOutboxProcessor> logger)
    : BackgroundService
{
    private readonly EmailOutboxProcessorOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "EmailOutboxProcessor started — interval={Interval}s batch={Batch} maxAttempts={Max}",
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
                logger.LogError(ex, "EmailOutboxProcessor unhandled error — will retry next tick");
            }
        }

        logger.LogInformation("EmailOutboxProcessor stopped");
    }

    // ──────────────────────────────────────────────────────────────────────────

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        List<Guid> ids;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            ids = await db.EmailOutboxMessages
                .IgnoreQueryFilters()
                .Where(m =>
                    (m.Status == EmailOutboxStatus.Pending || m.Status == EmailOutboxStatus.Failed)
                    && m.AttemptCount < _opts.MaxAttempts)
                .OrderBy(m => m.CreatedAt)
                .Take(_opts.BatchSize)
                .Select(m => m.Id)
                .ToListAsync(ct);
        }

        if (ids.Count == 0)
            return;

        logger.LogDebug("EmailOutboxProcessor picked up {Count} message(s)", ids.Count);

        foreach (var id in ids)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessOneAsync(id, ct);
        }
    }

    private async Task ProcessOneAsync(Guid messageId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IEmailProviderResolver>();
        var renderer = scope.ServiceProvider.GetRequiredService<ITemplateRenderer>();
        var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();

        // Load with tracking so mutations are persisted
        var message = await db.EmailOutboxMessages
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, ct);

        if (message is null
            || message.Status is EmailOutboxStatus.Sent or EmailOutboxStatus.DeadLettered)
            return;

        // Claim the message — prevents double-processing by concurrent instances
        message.MarkSending();
        db.EmailOutboxMessages.Update(message);
        await db.SaveChangesAsync(ct);

        try
        {
            var provider = await resolver.ResolveAsync(message.TenantId, ct);

            var rendered = await renderer.RenderAsync(
                message.TenantId, message.TemplateKey, message.Locale, message.TemplateDataJson, ct);

            var fromEmail = provider is { IsDefault: true }
                ? "noreply@sankore.io"
                : provider.FromEmail ?? "noreply@sankore.io";

            var request = new SendEmailRequest(
                MessageId: message.Id,
                TenantId: message.TenantId,
                FromEmail: fromEmail,
                FromName: provider.FromName ?? "Sankore",
                ReplyToEmail: provider.ReplyToEmail,
                ToEmail: message.RecipientEmail,
                ToName: message.RecipientName,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody);

            await sender.SendAsync(request, provider, ct);

            message.MarkSent();
            db.EmailOutboxMessages.Update(message);

            db.EmailDeliveryLogs.Add(EmailDeliveryLog.Record(
                message.TenantId,
                message.Id,
                EmailDeliveryEventType.Delivered,
                message.RecipientEmail,
                $"{{\"source\":\"outbox_processor\",\"provider\":\"{provider.ProviderType}\"}}"));

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Email sent | MessageId={Id} Tenant={TenantId} To={To} Provider={Provider}",
                message.Id, message.TenantId, message.RecipientEmail, provider.ProviderType);
        }
        catch (Exception ex)
        {
            var error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;

            logger.LogWarning(ex,
                "Email send failed | MessageId={Id} Attempt={Attempt}",
                message.Id, message.AttemptCount + 1);

            message.MarkFailed(error, _opts.MaxAttempts);
            db.EmailOutboxMessages.Update(message);

            db.EmailDeliveryLogs.Add(EmailDeliveryLog.Record(
                message.TenantId,
                message.Id,
                EmailDeliveryEventType.Rejected,
                message.RecipientEmail,
                $"{{\"source\":\"outbox_processor\",\"error\":{System.Text.Json.JsonSerializer.Serialize(error)}}}"));

            await db.SaveChangesAsync(ct);
        }
    }
}
