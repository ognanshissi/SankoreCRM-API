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
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox processing failed for {DbContext}", typeof(TDbContext).Name);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        var pending = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null && m.RetryCount < MaxRetries)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            try
            {
                var eventType = Type.GetType(message.EventType)
                    ?? throw new InvalidOperationException($"Unknown event type: {message.EventType}");

                var @event = System.Text.Json.JsonSerializer.Deserialize(message.PayloadJson, eventType)
                    ?? throw new InvalidOperationException("Failed to deserialize outbox payload.");

                await bus.Publish(@event, eventType, ct);

                message.ProcessedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception ex)
            {
                message.RetryCount++;
                message.LastError = ex.Message;
                logger.LogWarning(ex,
                    "Failed to publish outbox message {MessageId} (attempt {Attempt}/{Max})",
                    message.Id, message.RetryCount, MaxRetries);
            }
        }

        if (pending.Count > 0)
            await db.SaveChangesAsync(ct);
    }
}
