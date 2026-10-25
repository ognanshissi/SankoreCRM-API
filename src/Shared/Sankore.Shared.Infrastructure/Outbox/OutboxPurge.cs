namespace Sankore.Shared.Infrastructure.Outbox;

using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>
/// Deletes a module's outbox rows published more than <see cref="TechnicalRetention.ProcessedMessages"/>
/// ago. <see cref="OutboxProcessor{TDbContext}"/> only stamps <c>ProcessedAt</c>, so the table grew
/// with every event ever raised. A row never published (retries exhausted) is kept: it is the
/// evidence of an event that was lost, and somebody has to look at it.
/// </summary>
public sealed class OutboxPurge<TDbContext>(TDbContext db) : ITechnicalPurge
    where TDbContext : DbContext
{
    public string Name => $"{typeof(TDbContext).Name}.outbox_messages";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ProcessedMessages;
        return db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt != null && m.ProcessedAt < before)
            .ExecuteDeleteAsync(ct);
    }
}
