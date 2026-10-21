namespace Sankore.Modules.Leads.Features.WhatsApp;

using System.Globalization;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchLead;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Kernel;

/// <summary>
/// Who is told what, when a WhatsApp conversation needs a person (WA-BE-14, WA-BE-15, WA-BE-08).
///
/// <para>
/// <b>Two channels, every time</b>: an e-mail through M12/M08 for the person who is not looking at
/// the CRM, and a row in the bell — with its realtime nudge — for the one who is. Their subjects
/// and titles name nobody; the body names the prospect, never quotes them.
/// </para>
///
/// <para>
/// <b>The escalation also FINDS the agent</b> when nobody works the lead yet: M13's dispatching
/// assigns it like any lead, and the conversation follows. When nobody can be found either, the
/// agency's manager is told instead — a waiting customer must reach somebody.
/// </para>
/// </summary>
public sealed class WhatsAppNotificationsConsumer(
    IServiceScopeFactory scopeFactory,
    ILogger<WhatsAppNotificationsConsumer> logger)
    : IConsumer<WhatsAppConversationEscalatedEvent>,
      IConsumer<WhatsAppTakeoverBreachedEvent>,
      IConsumer<WhatsAppMessageFailedEvent>
{
    public const string EscalationTemplateKey = "whatsapp.escalation";
    public const string BreachTemplateKey = "whatsapp.takeover-breach";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public async Task Consume(ConsumeContext<WhatsAppConversationEscalatedEvent> context)
    {
        var (tenantId, conversationId) = (context.Message.TenantId, context.Message.ConversationId);
        var ct = context.CancellationToken;

        using var bgCtx = BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();

        var conversation = await db.WhatsAppConversations.AsTracking().FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        // Taken over, closed or handed back before this ran: nobody needs telling any more.
        if (conversation is null || conversation.Status != WhatsAppConversationStatus.PendingHuman) return;

        var lead = conversation.LeadId is { } leadId
            ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct)
            : null;

        if (conversation.TargetAgentId is null && lead is { IsDispatchable: true })
        {
            // Nobody works this lead: M13's dispatching chooses, exactly as for a captured lead.
            var dispatched = await sp.GetRequiredService<ISender>().Send(new DispatchLeadCommand(lead.Id, tenantId), ct);

            if (dispatched.IsSuccess)
            {
                conversation.SetTarget(dispatched.Value!.AgentId);
                await db.SaveChangesAsync(ct);
            }
        }

        var view = await DescribeAsync(sp, tenantId, conversation, lead, ct);

        var recipient = conversation.TargetAgentId
                        ?? await ManagerOfAsync(sp, tenantId, lead, conversation, ct);

        if (recipient is null)
        {
            logger.LogWarning(
                "WhatsApp conversation {ConversationId} of tenant {TenantId} waits for a person and nobody could be found to tell.",
                conversationId, tenantId);
            return;
        }

        var key = $"wa-escalation-{conversationId}-{conversation.PendingSince?.ToUnixTimeSeconds()}";

        await sp.GetRequiredService<IAdministrationModule>().NotifyUserAsync(
            new NotifyUserRequest(tenantId, recipient.Value, EscalationTemplateKey, key, new Dictionary<string, object>
            {
                ["lead_name"] = view.Name,
                ["product"] = view.Product,
                ["reason_label"] = ReasonLabel(conversation.EscalationReason),
                ["summary"] = conversation.EscalationSummary ?? string.Empty,
                ["sla_deadline"] = conversation.TakeoverDeadline is { } d
                    ? d.ToString("dd/MM/yyyy 'à' HH:mm", French) + " (UTC)"
                    : string.Empty,
                ["conversation_url"] = view.AbsoluteUrl,
            }),
            ct);

        await sp.GetRequiredService<INotificationsModule>().NotifyInAppAsync(
            new InAppNotificationRequest(
                tenantId, recipient.Value, "whatsapp.escalation",
                Title: "Un prospect attend votre réponse sur WhatsApp",
                Body: $"{view.Name} a écrit et attend un conseiller.",
                Link: view.Route,
                IdempotencyKey: key),
            ct);
    }

    public async Task Consume(ConsumeContext<WhatsAppTakeoverBreachedEvent> context)
    {
        var e = context.Message;
        var ct = context.CancellationToken;

        using var bgCtx = BackgroundJobContext.SetScope(e.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();

        var conversation = await db.WhatsAppConversations.FirstOrDefaultAsync(c => c.Id == e.ConversationId, ct);
        if (conversation is null) return;

        var lead = conversation.LeadId is { } leadId ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct) : null;
        var view = await DescribeAsync(sp, e.TenantId, conversation, lead, ct);

        var admin = sp.GetRequiredService<IAdministrationModule>();
        var inApp = sp.GetRequiredService<INotificationsModule>();

        var people = new[] { e.BreachedAgentId, e.NewAgentId }.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var names = people.Count == 0 ? new Dictionary<Guid, string>() : await admin.GetUserNamesAsync(e.TenantId, people, ct);

        var breachedName = e.BreachedAgentId is { } b ? names.GetValueOrDefault(b, "un agent") : "personne";
        var newName = e.NewAgentId is { } n ? names.GetValueOrDefault(n, "un agent") : "personne — à réattribuer";
        var key = $"wa-breach-{e.ConversationId}-{e.BreachedAt.ToUnixTimeSeconds()}";

        if (e.SupervisorId is { } supervisor)
        {
            await admin.NotifyUserAsync(
                new NotifyUserRequest(e.TenantId, supervisor, BreachTemplateKey, key, new Dictionary<string, object>
                {
                    ["lead_name"] = view.Name,
                    ["agent_name"] = breachedName,
                    ["new_agent_name"] = newName,
                    ["waited_minutes"] = e.WaitedMinutes,
                    ["conversation_url"] = view.AbsoluteUrl,
                }),
                ct);

            await inApp.NotifyInAppAsync(
                new InAppNotificationRequest(
                    e.TenantId, supervisor, "whatsapp.takeover-breach",
                    Title: "Conversation WhatsApp sans réponse dans les délais",
                    Body: $"{view.Name} attend depuis {e.WaitedMinutes} min. Réattribuée à {newName}.",
                    Link: view.Route,
                    IdempotencyKey: key),
                ct);
        }

        // The agent it now waits for learns it the way any expected agent does.
        if (e.NewAgentId is { } next && next != e.SupervisorId)
        {
            await inApp.NotifyInAppAsync(
                new InAppNotificationRequest(
                    e.TenantId, next, "whatsapp.escalation",
                    Title: "Un prospect attend votre réponse sur WhatsApp",
                    Body: $"{view.Name} vous a été réattribué : il attend depuis {e.WaitedMinutes} min.",
                    Link: view.Route,
                    IdempotencyKey: key + "-next"),
                ct);
        }
    }

    public async Task Consume(ConsumeContext<WhatsAppMessageFailedEvent> context)
    {
        var e = context.Message;
        var ct = context.CancellationToken;

        using var bgCtx = BackgroundJobContext.SetScope(e.TenantId, Guid.Empty, "SYSTEM");
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<LeadsDbContext>();

        var message = await db.WhatsAppMessages.FirstOrDefaultAsync(m => m.Id == e.MessageId, ct);
        if (message?.AuthorUserId is not { } author) return;

        var conversation = await db.WhatsAppConversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
        if (conversation is null) return;

        var lead = conversation.LeadId is { } leadId ? await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct) : null;
        var view = await DescribeAsync(sp, e.TenantId, conversation, lead, ct);

        await sp.GetRequiredService<INotificationsModule>().NotifyInAppAsync(
            new InAppNotificationRequest(
                e.TenantId, author, "whatsapp.message-failed",
                Title: "Message WhatsApp non délivré",
                Body: $"Votre message à {view.Name} n’a pas pu être délivré.",
                Link: view.Route,
                IdempotencyKey: $"wa-failed-{e.MessageId}"),
            ct);
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────

    private sealed record ConversationView(string Name, string Product, string Route, string AbsoluteUrl);

    /// <summary>Who the customer is, as an agent reads it, and where the conversation opens.</summary>
    private static async Task<ConversationView> DescribeAsync(
        IServiceProvider sp, Guid tenantId, WhatsAppConversation conversation, Lead? lead, CancellationToken ct)
    {
        var name = lead?.FullName;

        if (name is null && conversation.ClientId is { } clientId)
        {
            try
            {
                name = (await sp.GetRequiredService<ICustomersModule>().GetClientSummaryAsync(tenantId, clientId, ct))?.DisplayName;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        name ??= conversation.ContactName ?? "Un contact WhatsApp";

        var product = lead?.InterestedProduct is { Length: > 0 } p && !string.Equals(p, "Unknown", StringComparison.OrdinalIgnoreCase)
            ? p
            : string.Empty;

        // The lead's own tab when there is a lead; the inbox otherwise — a client's conversation
        // has no WhatsApp tab on the client record yet.
        var route = conversation.LeadId is { } leadId
            ? $"/leads/{leadId}/suivi/whatsapp"
            : $"/leads/whatsapp?conversation={conversation.Id}";

        var fqdn = (await sp.GetRequiredService<ITenantStore>().GetAsync(tenantId, ct))?.Fqdn?.TrimEnd('/') ?? string.Empty;

        return new ConversationView(name, product, route, fqdn + route);
    }

    /// <summary>The agency's manager — of the lead, else of the number that received the message.</summary>
    private static async Task<Guid?> ManagerOfAsync(
        IServiceProvider sp, Guid tenantId, Lead? lead, WhatsAppConversation conversation, CancellationToken ct)
    {
        var agencyId = lead?.AgencyId ?? lead?.PreferredAgencyId;

        if (agencyId is null)
        {
            var channel = await sp.GetRequiredService<IIntegrationModule>().Messaging.GetChannelAsync(tenantId, conversation.ConnectionId, ct);
            agencyId = channel?.AgencyId;
        }

        return agencyId is { } agency
            ? await sp.GetRequiredService<IAdministrationModule>().ResolveAgencyRecipientAsync(tenantId, agency, null, ct)
            : null;
    }

    internal static string ReasonLabel(string? reason) => Bot.WhatsAppBotReasons.Label(reason);
}
