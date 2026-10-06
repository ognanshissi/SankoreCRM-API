namespace Sankore.Shared.Infrastructure.Outbox;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Polls a single module's outbox table every few seconds and publishes
/// unprocessed rows to the broker via MassTransit. One instance of this
/// hosted service is registered PER MODULE (each closed over that module's
/// DbContext type), so modules never share a polling loop or a table.
///
/// In production this poll-based approach can be replaced by
/// Debezium/CDC on the outbox table for lower latency, without any change
/// to module code — this is purely an infrastructure concern.
/// </summary>
public sealed class OutboxProcessor<TDbContext>(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessor<TDbContext>> logger)
    : BackgroundService
    where TDbContext : DbContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int BatchSize = 50;
    private const int MaxRetries = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — not an error
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox processing failed for {DbContext}", typeof(TDbContext).Name);
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        var pending = await db.Set<OutboxMessage>()
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.ProcessedAt == null && m.RetryCount < MaxRetries)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            // Stop taking new work once shutdown starts, rather than beginning a publish that
            // will be cancelled a moment later and charged to the message as a failure.
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var eventType = Type.GetType(message.EventType)
                    ?? throw new InvalidOperationException($"Unknown event type: {message.EventType}");

                var @event = System.Text.Json.JsonSerializer.Deserialize(message.PayloadJson, eventType, OutboxJson.Options)
                    ?? throw new InvalidOperationException("Failed to deserialize outbox payload.");

                await bus.Publish(@event, eventType, ct);

                message.ProcessedAt = DateTimeOffset.UtcNow;
                db.Set<OutboxMessage>().Update(message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown is not a delivery failure, and must not spend one of the message's
                // attempts. It used to: the broad catch below counted a cancelled publish like any
                // other error, so a host stopped mid-send left the row with RetryCount incremented
                // and LastError "A task was canceled." Since the pending query filters on
                // RetryCount < MaxRetries, five such restarts would retire a message that had
                // never actually failed — silently, because a row that stops matching a WHERE
                // clause raises nothing. The next poll republishes it untouched instead.
                logger.LogDebug(
                    "Outbox publish of {MessageId} interrupted by shutdown; left for the next run",
                    message.Id);
                break;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;
                db.Set<OutboxMessage>().Update(message);
                logger.LogWarning(ex,
                    "Failed to publish outbox message {MessageId} (attempt {Attempt}/{Max})",
                    message.Id, message.RetryCount, MaxRetries);

                // The row simply stops being selected after this. Saying so once is the only
                // notice anyone gets that an event will never be delivered.
                if (message.RetryCount >= MaxRetries)
                {
                    logger.LogError(
                        "Outbox message {MessageId} ({EventType}) reached {Max} attempts and will "
                        + "NOT be retried. Last error: {LastError}",
                        message.Id, message.EventType, MaxRetries, message.LastError);
                }
            }
        }

        // CancellationToken.None on purpose. This save is bookkeeping for work already done: if a
        // shutdown cancels it, messages this batch successfully published stay unprocessed and are
        // published again on the next boot. The outbox is at-least-once, so a duplicate is legal —
        // but taking one for the sake of abandoning an UPDATE already in hand is a poor trade.
        if (pending.Count > 0)
            await db.SaveChangesAsync(CancellationToken.None);
    }
}
