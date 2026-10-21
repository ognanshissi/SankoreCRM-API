namespace Sankore.Modules.Leads.Features.WhatsApp;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.Crypto;
using Sankore.Modules.Leads.PublicApi;
using Sankore.Shared.Infrastructure.Crypto;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Realtime;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// Records what one WhatsApp delivery carried (WA-BE-07 → WA-BE-10), under the SYSTEM identity.
///
/// <para>
/// <b>Not an <c>ICommand</c>, deliberately.</b> The audit behaviour serialises a command into the
/// audit trail, and this one carries customers' messages in clear: an audit row is the last place
/// they belong. What the specification asks the audit trail to keep — sends, takeovers, consent
/// changes — are agents' acts, and they are audited where they happen. One <c>SaveChanges</c>
/// makes the batch atomic without the transaction behaviour.
/// </para>
/// </summary>
internal sealed record RecordMessagingEventsCommand(
    Guid TenantId,
    Guid ConnectionId,
    Guid? ConnectionAgencyId,
    MessagingEventBatch Batch) : IRequest<MessagingEventsOutcome>;

/// <summary>
/// <b>Attachment (WA-BE-09).</b> A message finds its conversation by the pair (institution number,
/// customer number). A number seen for the first time is looked up: the open lead most recently
/// active first, then a client; several matches attach to the first and flag the conversation
/// as ambiguous, so the screen says so. An unknown number becomes a lead when the tenant's
/// setting says so — the default — or stays unattached otherwise.
///
/// <para>
/// <b>Consent from an inbound message (WA-BE-05, « source : message entrant »).</b> A customer who
/// writes to the institution has asked to be talked to on WhatsApp; when the lead has no WhatsApp
/// consent row at all, one is recorded, with the message id as its proof. A WITHDRAWN consent is
/// left alone: an opt-out is never undone by a machine.
/// </para>
/// </summary>
internal sealed class RecordMessagingEventsHandler(
    LeadsDbContext db,
    IPhoneBlindIndexer leadPhoneIndexer,
    [FromKeyedServices(LeadsFieldProtection.Key)] IBlindIndexer blindIndexer,
    ICustomersModule customers,
    ILeadSettings settings,
    IRealtimeNotifier realtime,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    WhatsAppEscalation escalation,
    Bot.WhatsAppBotProfiles botProfiles,
    WhatsAppAudit audit,
    TimeProvider clock,
    ILogger<RecordMessagingEventsHandler> logger)
    : IRequestHandler<RecordMessagingEventsCommand, MessagingEventsOutcome>
{
    public async Task<MessagingEventsOutcome> Handle(RecordMessagingEventsCommand cmd, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pushes = new List<(Guid?[] To, string Event, Guid? LeadId, Guid ConversationId, Guid MessageId)>();
        var audits = new List<(string Action, Guid ConversationId, Guid? LeadId)>();

        var recorded = 0;
        var duplicates = 0;

        // Per message, in the order the customer wrote them, so the window and the thread advance
        // the way they happened.
        foreach (var inbound in cmd.Batch.Messages.OrderBy(m => m.SentAt))
        {
            var seen = await db.WhatsAppMessages
                .AnyAsync(m => m.ProviderMessageId == inbound.ProviderMessageId, ct)
                || db.WhatsAppMessages.Local.Any(m => m.ProviderMessageId == inbound.ProviderMessageId);

            if (seen)
            {
                duplicates++;
                continue;
            }

            var phone = WhatsAppNumbers.ToE164(inbound.FromE164, defaultCallingCode: null);

            if (phone is null)
            {
                logger.LogWarning("WhatsApp message {MessageId}: the sender is not an E.164 number; dropped.", inbound.ProviderMessageId);
                continue;
            }

            var phoneIndex = blindIndexer.Compute(BlindIndexPurpose.Phone, phone);

            var conversation = db.WhatsAppConversations.Local
                .FirstOrDefault(c => c.ConnectionId == cmd.ConnectionId && c.ContactPhoneBlindIndex == phoneIndex)
                ?? await db.WhatsAppConversations
                    .AsTracking()
                    .FirstOrDefaultAsync(c => c.ConnectionId == cmd.ConnectionId && c.ContactPhoneBlindIndex == phoneIndex, ct);

            if (conversation is null)
            {
                conversation = await OpenAsync(cmd, phone, phoneIndex, inbound, now, ct);
                db.WhatsAppConversations.Add(conversation);
            }

            var message = WhatsAppMessage.Inbound(
                cmd.TenantId,
                conversation.Id,
                inbound.ProviderMessageId,
                KindOf(inbound.Type),
                inbound.Text,
                inbound.MediaId,
                inbound.MediaMimeType,
                inbound.MediaFileName,
                inbound.ReplyToProviderMessageId,
                inbound.SentAt,
                now);

            db.WhatsAppMessages.Add(message);

            // WA-BE-20: our own encrypted copy, fetched out of the webhook's way.
            if (message.MediaId is not null)
                await publisher.PublishAsync(new WhatsAppMediaReceivedEvent(cmd.TenantId, message.Id), ct);

            Lead? lead = null;

            if (conversation.LeadId is { } leadId)
            {
                lead = db.Leads.Local.FirstOrDefault(l => l.Id == leadId)
                       ?? await db.Leads.AsTracking().FirstOrDefaultAsync(l => l.Id == leadId, ct);
            }

            // WA-BE-18: STOP is read BEFORE anything else — before the escalation, before the bot
            // of lot 3. An opt-out must never be answered by « un conseiller vous répond ».
            if (WhatsAppOptOut.IsStop(inbound.Text) && message.Kind is WhatsAppMessageKind.Text or WhatsAppMessageKind.Button)
            {
                conversation.RecordInbound(inbound.SentAt, inbound.ProfileName);
                await OptOutAsync(cmd.TenantId, conversation, lead, inbound.ProviderMessageId, now, ct);
                audits.Add((WhatsAppAudit.OptOutReceived, conversation.Id, conversation.LeadId));

                pushes.Add(([lead?.CurrentAssignedId, lead?.OwnerId, conversation.OwnerUserId, conversation.TargetAgentId],
                    WhatsAppRealtime.MessageReceived, conversation.LeadId, conversation.Id, message.Id));

                recorded++;
                continue;
            }

            // The bot is asked only when it could answer: a conversation already with a person
            // never needs the profile, and reading it per message would cost a query for nothing.
            var botProfile = conversation.Status is WhatsAppConversationStatus.Human or WhatsAppConversationStatus.PendingHuman
                ? null
                : await botProfiles.ResolveActiveAsync(cmd.TenantId, lead, ct);

            var routing = conversation.RecordInbound(inbound.SentAt, inbound.ProfileName, botAvailable: botProfile is not null);

            if (lead is not null)
            {
                if (await RecordConsentIfNoneAsync(lead, inbound.ProviderMessageId, ct))
                    audits.Add((WhatsAppAudit.ConsentFromInboundMessage, conversation.Id, lead.Id));

                // The customer answered: contact is established. Forward only, and a closed
                // lead is left alone — the conversation still records the message.
                lead.RecordContactEstablished();
            }

            switch (routing)
            {
                case WhatsAppInboundRouting.Escalate:
                    await escalation.EscalateAsync(
                        cmd.TenantId, conversation, lead, "CUSTOMER_REPLIED", summary: null, now,
                        profile: null, tellCustomer: true, ct);
                    break;

                case WhatsAppInboundRouting.BotStarted or WhatsAppInboundRouting.BotContinues:
                    // The welcome goes now, from here: the customer sees an answer within the
                    // second while the model thinks.
                    if (routing == WhatsAppInboundRouting.BotStarted && !string.IsNullOrWhiteSpace(botProfile!.WelcomeMessage))
                        await escalation.QueueAutomaticAsync(cmd.TenantId, conversation, WhatsAppAuthor.Bot, botProfile.WelcomeMessage, now, ct);

                    await publisher.PublishAsync(new WhatsAppBotTurnRequestedEvent(cmd.TenantId, conversation.Id, message.Id), ct);
                    break;
            }

            pushes.Add(([lead?.CurrentAssignedId, lead?.OwnerId, conversation.OwnerUserId, conversation.TargetAgentId],
                WhatsAppRealtime.MessageReceived, conversation.LeadId, conversation.Id, message.Id));

            recorded++;
        }

        var applied = 0;
        var unmatched = 0;

        foreach (var status in cmd.Batch.Statuses.OrderBy(s => s.At))
        {
            var message = db.WhatsAppMessages.Local.FirstOrDefault(m => m.ProviderMessageId == status.ProviderMessageId)
                ?? await db.WhatsAppMessages
                    .AsTracking()
                    .FirstOrDefaultAsync(m => m.ProviderMessageId == status.ProviderMessageId, ct);

            if (message is null)
            {
                unmatched++;
                continue;
            }

            if (!message.ApplyStatus(status.Status, status.At, status.ErrorCode, status.ErrorDetail)) continue;

            applied++;

            // WA-BE-08: a failure after acceptance reaches its sender in the bell, not only as a
            // red bubble they would have to be looking at.
            if (message.Status == WhatsAppMessageStatus.Failed && message.AuthorUserId is not null)
                await publisher.PublishAsync(new WhatsAppMessageFailedEvent(cmd.TenantId, message.Id), ct);

            var conversation = await db.WhatsAppConversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
            pushes.Add(([message.AuthorUserId], WhatsAppRealtime.MessageStatus,
                conversation?.LeadId, message.ConversationId, message.Id));
        }

        await db.SaveChangesAsync(ct);

        // AFTER the commit, like the pushes: an audit row for a consent change that rolled back
        // would record something that did not happen.
        foreach (var (action, conversationId, leadId) in audits)
            await audit.SystemAsync(cmd.TenantId, action, conversationId, leadId, ct);

        // AFTER the commit: a browser nudged before it would read a thread that does not hold the
        // message yet.
        foreach (var push in pushes)
        {
            await WhatsAppRealtime.PushAsync(
                realtime, logger, cmd.TenantId, push.To, push.Event, push.LeadId, push.ConversationId, push.MessageId, ct);
        }

        return new MessagingEventsOutcome(recorded, duplicates, applied, unmatched);
    }

    /// <summary>
    /// STOP (WA-BE-18): the WhatsApp consent is withdrawn at once — or a refusal recorded when
    /// there was none to withdraw — the customer is told, and the conversation closes. Every send
    /// is then blocked by the consent rule itself, templates included.
    /// </summary>
    private async Task OptOutAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, string providerMessageId,
        DateTimeOffset now, CancellationToken ct)
    {
        if (lead is not null)
        {
            var consents = await db.LeadConsents
                .AsTracking()
                .Where(c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null)
                .ToListAsync(ct);

            var active = consents.Where(c => c.Status == ConsentStatus.Active).ToList();

            foreach (var consent in active)
                consent.Withdraw(Guid.Empty, clock, $"STOP reçu sur WhatsApp ({providerMessageId})");

            if (consents.Count == 0)
            {
                db.LeadConsents.Add(LeadConsent.Create(
                    tenantId: tenantId,
                    leadId: lead.Id,
                    type: ConsentType.WhatsAppContact,
                    channel: ConsentChannel.WhatsApp,
                    recordedBy: Guid.Empty,
                    clock: clock,
                    proofReference: $"STOP reçu sur WhatsApp ({providerMessageId})",
                    granted: false));
            }
        }

        var confirmation = await settings.GetStringAsync(tenantId, LeadSettingKeys.WhatsAppOptOutConfirmation, ct);

        if (!string.IsNullOrWhiteSpace(confirmation))
            await escalation.QueueAutomaticAsync(tenantId, conversation, WhatsAppAuthor.System, confirmation, now, ct);

        conversation.Close("OPT_OUT", closedBy: null, now);
        await WhatsAppHandovers.EndAsync(db, conversation.Id, WhatsAppHandoverEnd.Closed, null, now, ct);
    }

    private async Task<WhatsAppConversation> OpenAsync(
        RecordMessagingEventsCommand cmd,
        string phone,
        string phoneIndex,
        InboundMessagingMessage inbound,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var leads = await OpenLeadsByPhone.FindAsync(db, leadPhoneIndexer, cmd.TenantId, phone, ct);

        if (leads.Count > 0)
        {
            return WhatsAppConversation.Open(
                cmd.TenantId, cmd.ConnectionId, phone, phoneIndex, leads[0].LeadId, clientId: null,
                inbound.ProfileName, ambiguousMatch: leads.Count > 1, now);
        }

        IReadOnlyList<CustomerMatch> clients;

        try
        {
            clients = await customers.FindMatchesAsync(cmd.TenantId, phone, null, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // M01 down must not lose the customer's message: it is filed unattached, and the
            // number is matched again when somebody attaches it.
            logger.LogWarning("WhatsApp: the client lookup failed ({Reason}); the conversation stays unattached.", ex.GetType().Name);
            clients = [];
        }

        if (clients.Count > 0)
        {
            return WhatsAppConversation.Open(
                cmd.TenantId, cmd.ConnectionId, phone, phoneIndex, leadId: null, clients[0].ClientId,
                inbound.ProfileName, ambiguousMatch: clients.Count > 1, now);
        }

        var createLead = await settings.GetBoolAsync(cmd.TenantId, LeadSettingKeys.WhatsAppCreateLeadForUnknownNumber, ct);

        Guid? newLeadId = null;

        if (createLead)
        {
            var lead = Lead.Capture(
                tenantId: cmd.TenantId,
                // A profile name is what the customer chose to show, not an identity; the agent
                // confirms it on the first real exchange. Without one, a dated placeholder nobody
                // can mistake for a name somebody gave.
                fullName: string.IsNullOrWhiteSpace(inbound.ProfileName)
                    ? $"Contact WhatsApp du {inbound.SentAt:dd/MM/yyyy HH:mm}"
                    : inbound.ProfileName.Trim(),
                phoneNumber: phone,
                source: LeadSource.WhatsApp,
                // "Unknown" resolves to no product line by design — see CaptureFromMissedCallAsync.
                interestedProduct: "Unknown",
                preferredLanguage: LanguageCode.Default,
                location: new GeoPoint(0, 0),
                preferredAgencyId: cmd.ConnectionAgencyId,
                clock: clock,
                channel: LeadChannel.WhatsApp,
                agencyId: cmd.ConnectionAgencyId,
                phoneBlindIndex: leadPhoneIndexer.Compute(phone));

            db.Leads.Add(lead);
            newLeadId = lead.Id;

            // The capture's own event, so auto-dispatch (when the tenant turned it on) routes the
            // new lead to an agent exactly as it would a web-form lead.
            await publisher.PublishAsync(
                new CaptureLead.Events.LeadCapturedEvent(
                    LeadId: lead.Id,
                    TenantId: cmd.TenantId,
                    Source: lead.Source.ToString(),
                    HasExplicitOwner: false,
                    DuplicateSuspected: false),
                ct);
        }

        return WhatsAppConversation.Open(
            cmd.TenantId, cmd.ConnectionId, phone, phoneIndex, newLeadId, clientId: null,
            inbound.ProfileName, ambiguousMatch: false, now);
    }

    private async Task<bool> RecordConsentIfNoneAsync(Lead lead, string providerMessageId, CancellationToken ct)
    {
        var hasAny = await db.LeadConsents.AnyAsync(
                         c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null, ct)
                     || db.LeadConsents.Local.Any(
                         c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null);

        if (hasAny) return false;

        var consent = LeadConsent.Create(
            tenantId: lead.TenantId,
            leadId: lead.Id,
            type: ConsentType.WhatsAppContact,
            channel: ConsentChannel.WhatsApp,
            // SYSTEM: nobody recorded it, the customer's own message did. Never an agent's id.
            recordedBy: Guid.Empty,
            clock: clock,
            proofReference: $"Message WhatsApp entrant {providerMessageId}");

        db.LeadConsents.Add(consent);
        lead.RaiseConsentRecordedEvent(consent.Id, ConsentType.WhatsAppContact.ToString());
        return true;
    }

    private static WhatsAppMessageKind KindOf(string type) => type switch
    {
        "text" => WhatsAppMessageKind.Text,
        "image" => WhatsAppMessageKind.Image,
        "document" => WhatsAppMessageKind.Document,
        "audio" => WhatsAppMessageKind.Audio,
        "video" => WhatsAppMessageKind.Video,
        "sticker" => WhatsAppMessageKind.Sticker,
        "location" => WhatsAppMessageKind.Location,
        "contacts" => WhatsAppMessageKind.Contacts,
        "button" or "interactive" => WhatsAppMessageKind.Button,
        "reaction" => WhatsAppMessageKind.Reaction,
        _ => WhatsAppMessageKind.Unsupported,
    };
}
