namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.WhatsAppMedia;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// The WhatsApp retention (WA-BE-21), nightly per tenant, after the lead anonymisation.
///
/// <para>
/// <b>Two clocks, because two kinds of data.</b> A conversation filed on a LEAD is anonymised with
/// that lead by <c>AnonymizeClosedLeadsJob</c> — « alignée sur celle des leads M13 » — and is not
/// touched here. A conversation filed on NO lead (a client's, an unattached number's) has no lead
/// to follow, and used to be kept for ever: it is anonymised here after
/// <c>whatsapp-retention-months</c> of silence. Media have their own, shorter clock,
/// <c>whatsapp-media-retention-days</c>: a photograph of an identity document sent on WhatsApp is
/// the most sensitive thing in the thread and is the first to go — and goes at once when its
/// conversation was anonymised.
/// </para>
///
/// <para>
/// <b>Zero or negative SKIPS</b>, like the lead retention: that is what a settings typo produces,
/// and the deletion is irreversible.
/// </para>
///
/// <para>
/// <b>Our copy always goes; the provider's on a best-effort basis.</b> The encrypted object is
/// deleted, the provider is asked to delete its own (inbound media expire there after thirty days
/// anyway), and the message records the purge so the thread says « supprimé » instead of offering
/// a dead link. A provider refusal is logged and never keeps our copy alive.
/// </para>
/// </summary>
public sealed class WhatsAppRetentionJob(IServiceScopeFactory scopeFactory, ILogger<WhatsAppRetentionJob> logger)
{
    public const string RecurringJobPrefix = "whatsapp-retention";
    public const string CronExpression = "15 4 * * *";

    /// <summary>Rows per run and per kind — a backlog drains over a few nights instead of one long transaction.</summary>
    internal const int BatchSize = 500;

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await RunAsync(scope.ServiceProvider, tenantId, CancellationToken.None);
    }

    internal async Task<(int Conversations, int Media)> RunAsync(IServiceProvider sp, Guid tenantId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<LeadsDbContext>();
        var settings = sp.GetRequiredService<ILeadSettings>();
        var now = sp.GetRequiredService<TimeProvider>().GetUtcNow();

        var conversations = 0;
        var months = await settings.GetIntAsync(tenantId, LeadSettingKeys.WhatsAppRetentionMonths, ct);

        if (months > 0)
            conversations = await AnonymizeLeadlessAsync(db, tenantId, now.AddMonths(-months), now, ct);
        else
            logger.LogWarning("WhatsApp retention of tenant {TenantId} skipped: {Setting} is {Value}.", tenantId, LeadSettingKeys.WhatsAppRetentionMonths, months);

        var media = 0;
        var days = await settings.GetIntAsync(tenantId, LeadSettingKeys.WhatsAppMediaRetentionDays, ct);

        if (days > 0)
            media = await PurgeMediaAsync(sp, db, tenantId, now.AddDays(-days), now, ct);
        else
            logger.LogWarning("WhatsApp media purge of tenant {TenantId} skipped: {Setting} is {Value}.", tenantId, LeadSettingKeys.WhatsAppMediaRetentionDays, days);

        if (conversations > 0 || media > 0)
            logger.LogInformation("WhatsApp retention of tenant {TenantId}: {Conversations} conversation(s) anonymised, {Media} media purged.", tenantId, conversations, media);

        return (conversations, media);
    }

    private static async Task<int> AnonymizeLeadlessAsync(
        LeadsDbContext db, Guid tenantId, DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.WhatsAppConversations
            .AsTracking()
            .Where(c => c.LeadId == null && c.AnonymizedAt == null && c.LastMessageAt < cutoff)
            .OrderBy(c => c.LastMessageAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var ids = due.Select(c => c.Id).ToList();

        var messages = await db.WhatsAppMessages.AsTracking().Where(m => ids.Contains(m.ConversationId)).ToListAsync(ct);
        var turns = await db.WhatsAppBotTurns.AsTracking().Where(t => ids.Contains(t.ConversationId)).ToListAsync(ct);

        foreach (var conversation in due) conversation.Anonymize(now);
        foreach (var message in messages) message.Anonymize();
        foreach (var turn in turns) turn.Anonymize();

        // RunAsync carries the tenant through the scope; the intakes take it explicitly.
        await Complaints.WhatsAppComplaintIntakes.AnonymizeAsync(db, due[0].TenantId, ids, now, ct);

        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task<int> PurgeMediaAsync(
        IServiceProvider sp, LeadsDbContext db, Guid tenantId, DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        var due = await (
                from m in db.WhatsAppMessages.AsTracking()
                join c in db.WhatsAppConversations on m.ConversationId equals c.Id
                where m.MediaPurgedAt == null
                      && (m.MediaStorageRef != null || m.MediaId != null)
                      && (m.OccurredAt < cutoff || c.AnonymizedAt != null)
                orderby m.OccurredAt
                select new { Message = m, c.ConnectionId })
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        var store = sp.GetService<IWhatsAppMediaStore>();
        var messaging = sp.GetRequiredService<IIntegrationModule>().Messaging;

        foreach (var item in due)
        {
            var message = item.Message;

            if (message.MediaStorageRef is { } storageRef)
            {
                if (store is null)
                {
                    // A stored copy and no key to reach it: leave the row, say so — marking it
                    // purged would claim a deletion that did not happen.
                    logger.LogError("WhatsApp media {MessageId} is due for deletion but no media store is configured.", message.Id);
                    continue;
                }

                await store.DeleteAsync(tenantId, storageRef, ct);
            }

            if (message.MediaId is { } mediaId)
            {
                var deleted = await messaging.DeleteMediaAsync(tenantId, item.ConnectionId, mediaId, ct);

                if (deleted.IsFailure)
                    logger.LogWarning("WhatsApp media {MessageId}: the provider did not confirm its deletion ({Code}).", message.Id, deleted.Code);
            }

            message.MarkMediaPurged(now);
        }

        await db.SaveChangesAsync(ct);
        return due.Count(d => d.Message.MediaPurgedAt is not null);
    }
}
