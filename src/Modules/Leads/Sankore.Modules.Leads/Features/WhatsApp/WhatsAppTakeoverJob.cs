namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// The two clocks of a WhatsApp conversation, swept every two minutes per tenant.
///
/// <para>
/// <b>The takeover SLA (WA-BE-15).</b> A conversation still waiting for a person past its deadline
/// is marked breached — once — its lead is redispatched by M13 to ANOTHER agent (the one who let it
/// wait is excluded, and an agent-less result leaves the lead where it is rather than queueing
/// it), and the supervisor is told. When nobody else can take it, the agency's manager becomes the
/// expected agent, with no new SLA: the manager is the end of the chain, not its next link.
/// </para>
///
/// <para>
/// <b>The 24-hour window (WA-BE-10).</b> Whoever holds a conversation is told, once per window, when
/// it closes within the hour — after that, only a template can reach the customer.
/// </para>
/// </summary>
public sealed class WhatsAppTakeoverJob(IServiceScopeFactory scopeFactory, ILogger<WhatsAppTakeoverJob> logger)
{
    public const string RecurringJobPrefix = "whatsapp-takeover";
    public const string CronExpression = "*/2 * * * *";

    /// <summary>How long before the window closes its holder is warned.</summary>
    internal static readonly TimeSpan WindowWarning = TimeSpan.FromHours(1);

    public async Task ExecuteAsync(Guid tenantId)
    {
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        await RunAsync(scope.ServiceProvider, tenantId, CancellationToken.None);
    }

    internal async Task RunAsync(IServiceProvider sp, Guid tenantId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<LeadsDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();

        var breached = await db.WhatsAppConversations
            .AsTracking()
            .Where(c => c.Status == WhatsAppConversationStatus.PendingHuman
                     && c.TakeoverBreachedAt == null
                     && c.TakeoverDeadline != null
                     && c.TakeoverDeadline <= now)
            .OrderBy(c => c.TakeoverDeadline)
            .Take(100)
            .ToListAsync(ct);

        foreach (var conversation in breached)
        {
            try
            {
                await BreachAsync(sp, db, tenantId, conversation, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One conversation must not stop the others; it is retried at the next sweep
                // because nothing was saved for it.
                logger.LogError(ex, "WhatsApp takeover SLA of conversation {ConversationId} could not be processed.", conversation.Id);
            }
        }

        await WarnClosingWindowsAsync(sp, db, tenantId, now, ct);
        await HandOverExpiringBotWindowsAsync(sp, db, tenantId, now, ct);
    }

    /// <summary>
    /// The window rule of WA-BE-13: a conversation the bot still holds goes to a person while the
    /// person can still answer freely — past the window only a template reaches the customer.
    /// Evaluated here and not on a message, because a customer's message reopens the window for
    /// 24 hours: the rule can only ever fire on silence. The customer is NOT sent the waiting
    /// message — they said nothing, and « un conseiller vous répond » out of the blue is noise.
    /// </summary>
    private async Task HandOverExpiringBotWindowsAsync(
        IServiceProvider sp, LeadsDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        // The longest threshold a profile may set: anything further out cannot qualify yet.
        var horizon = now + TimeSpan.FromHours(23);

        var candidates = await db.WhatsAppConversations
            .AsTracking()
            .Where(c => c.Status == WhatsAppConversationStatus.Bot
                     && c.WindowExpiresAt != null
                     && c.WindowExpiresAt > now
                     && c.WindowExpiresAt <= horizon)
            .OrderBy(c => c.WindowExpiresAt)
            .Take(100)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var profiles = sp.GetRequiredService<Bot.WhatsAppBotProfiles>();
        var escalation = sp.GetRequiredService<WhatsAppEscalation>();

        foreach (var conversation in candidates)
        {
            try
            {
                var lead = conversation.LeadId is { } leadId
                    ? await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct)
                    : null;

                var profile = await profiles.ResolveActiveAsync(tenantId, lead, ct);

                // No active profile any more: the next message escalates on its own; nothing to
                // hand over on silence.
                if (profile is null) continue;

                var verdict = Bot.WhatsAppBotRules.WindowExpiring(conversation.WindowExpiresAt, profile, now);
                if (verdict is null) continue;

                await escalation.EscalateAsync(tenantId, conversation, lead, verdict.Reason, verdict.Summary, now,
                    profile, tellCustomer: false, ct);

                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "WhatsApp bot window handover of conversation {ConversationId} failed.", conversation.Id);
            }
        }
    }

    private static async Task BreachAsync(
        IServiceProvider sp, LeadsDbContext db, Guid tenantId, WhatsAppConversation conversation,
        DateTimeOffset now, CancellationToken ct)
    {
        if (!conversation.MarkTakeoverBreached(now)) return;

        var handover = await WhatsAppHandovers.FindOpenAsync(db, conversation.Id, ct);
        handover?.MarkBreached(now);

        var breachedAgent = conversation.TargetAgentId;
        var waited = (int)Math.Round((now - (conversation.PendingSince ?? now)).TotalMinutes);

        var lead = conversation.LeadId is { } leadId
            ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;

        var admin = sp.GetRequiredService<IAdministrationModule>();
        var agencyId = lead?.AgencyId ?? lead?.PreferredAgencyId;

        if (agencyId is null)
        {
            var channel = await sp.GetRequiredService<IIntegrationModule>().Messaging.GetChannelAsync(tenantId, conversation.ConnectionId, ct);
            agencyId = channel?.AgencyId;
        }

        var supervisor = agencyId is { } agency
            ? await admin.ResolveAgencyRecipientAsync(tenantId, agency, null, ct)
            : null;

        Guid? newAgent = null;

        if (lead is { IsDispatchable: true })
        {
            var dispatched = await sp.GetRequiredService<ISender>().Send(
                new DispatchLeadCommand(
                    lead.Id, tenantId,
                    OverrideReason: null,
                    ExcludedAgentIds: breachedAgent is { } b ? [b] : null,
                    QueueWhenNoAgent: false),
                ct);

            if (dispatched.IsSuccess) newAgent = dispatched.Value!.AgentId;
        }

        if (newAgent is { } agent)
        {
            var minutes = await sp.GetRequiredService<ILeadSettings>()
                .GetIntAsync(tenantId, LeadSettingKeys.WhatsAppTakeoverSlaMinutes, ct);

            conversation.Reassign(agent, minutes > 0 ? TimeSpan.FromMinutes(minutes) : null, now);
        }
        else if (supervisor is { } manager)
        {
            // The end of the chain: no new SLA, or the sweep would breach the manager next.
            conversation.Reassign(manager, sla: null, now);
            newAgent = manager;
        }

        handover?.MoveDeadline(conversation.TakeoverDeadline);

        // Reassign starts a fresh SLA for the new agent (or none for the manager): the next breach,
        // if any, is about THEM, and is told once like this one.
        await sp.GetRequiredKeyedService<IEventPublisher>(nameof(LeadsDbContext)).PublishAsync(
            new WhatsAppTakeoverBreachedEvent(tenantId, conversation.Id, breachedAgent, newAgent, supervisor, waited, now), ct);

        await db.SaveChangesAsync(ct);
    }

    private static async Task WarnClosingWindowsAsync(
        IServiceProvider sp, LeadsDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var limit = now + WindowWarning;

        var closing = await db.WhatsAppConversations
            .AsTracking()
            .Where(c => c.Status == WhatsAppConversationStatus.Human
                     && c.OwnerUserId != null
                     && c.WindowWarningSentAt == null
                     && c.WindowExpiresAt != null
                     && c.WindowExpiresAt > now
                     && c.WindowExpiresAt <= limit)
            .Take(200)
            .ToListAsync(ct);

        if (closing.Count == 0) return;

        foreach (var conversation in closing)
            conversation.MarkWindowWarningSent(now);

        await db.SaveChangesAsync(ct);

        var notifications = sp.GetRequiredService<INotificationsModule>();

        foreach (var conversation in closing)
        {
            var route = conversation.LeadId is { } leadId
                ? $"/leads/{leadId}/suivi/whatsapp"
                : $"/leads/whatsapp?conversation={conversation.Id}";

            await notifications.NotifyInAppAsync(
                new InAppNotificationRequest(
                    tenantId, conversation.OwnerUserId!.Value, "whatsapp.window-expiring",
                    Title: "Fenêtre WhatsApp bientôt fermée",
                    Body: "Moins d’une heure pour répondre librement ; ensuite, seul un template pourra être envoyé.",
                    Link: route,
                    IdempotencyKey: $"wa-window-{conversation.Id}-{conversation.WindowExpiresAt!.Value.ToUnixTimeSeconds()}"),
                ct);
        }
    }
}
