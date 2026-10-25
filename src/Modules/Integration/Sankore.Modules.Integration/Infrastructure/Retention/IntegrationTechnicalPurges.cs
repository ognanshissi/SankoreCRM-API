namespace Sankore.Modules.Integration.Infrastructure.Retention;

using Microsoft.EntityFrameworkCore;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>Idempotency rows of messages consumed long ago: no broker redelivers that late.</summary>
internal sealed class IntegrationInboxPurge(IntegrationDbContext db) : ITechnicalPurge
{
    public string Name => "integration.inbox_messages";

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var before = now - TechnicalRetention.ProcessedMessages;
        return db.InboxMessages.IgnoreQueryFilters().Where(m => m.ReceivedAt < before).ExecuteDeleteAsync(ct);
    }
}
