namespace Sankore.Modules.Integration.Features.ConnectedAccounts;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// A lead or a client was erased (data-purge): every e-mail kept BECAUSE of them goes — whole, not
/// only the link, since what they wrote and what was written to them is their data. A message that
/// also named another lead or client goes too: it still holds the erased person's words. The
/// mailbox itself keeps the e-mail; only the CRM's copy is erased. Idempotent: a replay finds
/// nothing left.
/// </summary>
internal static class ConnectedMailErasure
{
    public static async Task EraseAsync(
        IServiceScopeFactory scopeFactory, ILogger logger, Guid tenantId, MailLinkEntity type, Guid entityId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || entityId == Guid.Empty) return;

        // Before the scope: ITenantContext and ICurrentUser are built from this ambient context.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        var messageIds = await db.ConnectedMailLinks
            .Where(l => l.EntityType == type && l.EntityId == entityId)
            .Select(l => l.MessageId)
            .Distinct()
            .ToListAsync(ct);

        if (messageIds.Count == 0) return;

        db.ConnectedMailLinks.RemoveRange(await db.ConnectedMailLinks.AsTracking().Where(l => messageIds.Contains(l.MessageId)).ToListAsync(ct));
        db.ConnectedMailMessages.RemoveRange(await db.ConnectedMailMessages.AsTracking().Where(m => messageIds.Contains(m.Id)).ToListAsync(ct));
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Erasure of {Type} {EntityId}: {Count} kept e-mail(s) deleted.", type, entityId, messageIds.Count);
    }
}

public sealed class LeadErasedMailConsumer(IServiceScopeFactory scopeFactory, ILogger<LeadErasedMailConsumer> logger)
    : IConsumer<LeadErasedEvent>
{
    public Task Consume(ConsumeContext<LeadErasedEvent> context) =>
        ConnectedMailErasure.EraseAsync(scopeFactory, logger, context.Message.TenantId, MailLinkEntity.Lead, context.Message.LeadId, context.CancellationToken);
}

public sealed class ClientAnonymizedMailConsumer(IServiceScopeFactory scopeFactory, ILogger<ClientAnonymizedMailConsumer> logger)
    : IConsumer<ClientAnonymizedEvent>
{
    public Task Consume(ConsumeContext<ClientAnonymizedEvent> context) =>
        ConnectedMailErasure.EraseAsync(scopeFactory, logger, context.Message.TenantId, MailLinkEntity.Client, context.Message.ClientId, context.CancellationToken);
}
