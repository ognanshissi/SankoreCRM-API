namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// The lead of an unknown WhatsApp number — created at the first message, or once the bot has
/// collected the minimum (<see cref="LeadSettingKeys.WhatsAppCreateLeadAfterCollection"/>). One
/// body for both moments, so a lead created later is the same lead the first message would have
/// created, plus what the customer said in between.
///
/// <para>
/// <b>Never saves.</b> The webhook handler and the bot engine each commit their own unit of work,
/// with the conversation, the messages and the turn in it.
/// </para>
/// </summary>
internal sealed class WhatsAppProspects(
    LeadsDbContext db,
    IPhoneBlindIndexer leadPhoneIndexer,
    IIntegrationModule integration,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    WhatsAppBotFieldValues fieldValues,
    TimeProvider clock,
    ILogger<WhatsAppProspects> logger)
{
    /// <summary>A new WhatsApp lead, added to the context, its capture event published.</summary>
    public async Task<Lead> CaptureAsync(
        Guid tenantId, string phone, string? profileName, DateTimeOffset sentAt, Guid? agencyId, CancellationToken ct)
    {
        var lead = Lead.Capture(
            tenantId: tenantId,
            // A profile name is what the customer chose to show, not an identity; the agent
            // confirms it on the first real exchange. Without one, a dated placeholder nobody
            // can mistake for a name somebody gave.
            fullName: string.IsNullOrWhiteSpace(profileName)
                ? $"Contact WhatsApp du {sentAt:dd/MM/yyyy HH:mm}"
                : profileName.Trim(),
            phoneNumber: phone,
            source: LeadSource.WhatsApp,
            // "Unknown" resolves to no product line by design — see CaptureFromMissedCallAsync.
            interestedProduct: "Unknown",
            preferredLanguage: LanguageCode.Default,
            location: new GeoPoint(0, 0),
            preferredAgencyId: agencyId,
            clock: clock,
            channel: LeadChannel.WhatsApp,
            agencyId: agencyId,
            phoneBlindIndex: leadPhoneIndexer.Compute(phone));

        db.Leads.Add(lead);

        // The capture's own event, so auto-dispatch (when the tenant turned it on) routes the new
        // lead to an agent exactly as it would a web-form lead — and, for a deferred lead, only now.
        await publisher.PublishAsync(
            new CaptureLead.Events.LeadCapturedEvent(
                LeadId: lead.Id,
                TenantId: tenantId,
                Source: lead.Source.ToString(),
                HasExplicitOwner: false,
                DuplicateSuspected: false),
            ct);

        return lead;
    }

    /// <summary>
    /// Turns a conversation the bot held without a lead into one filed on a lead: the open lead the
    /// number now matches if an agent created one meanwhile, a new one otherwise — with what the
    /// bot collected, the customer's consent to be contacted on WhatsApp, and contact established.
    /// </summary>
    /// <returns>The lead, and whether a consent row was recorded (the caller audits it after its commit).</returns>
    public async Task<(Lead Lead, bool ConsentRecorded)> CreateFromConversationAsync(
        Guid tenantId, WhatsAppConversation conversation, BotFieldContext ctx, Guid? agencyId, CancellationToken ct)
    {
        var pending = conversation.PendingLeadFields();
        Lead lead;
        bool created;

        var existing = await OpenLeadsByPhone.FindAsync(db, leadPhoneIndexer, tenantId, conversation.ContactPhone, ct);

        if (existing.Count > 0)
        {
            lead = db.Leads.Local.FirstOrDefault(l => l.Id == existing[0].LeadId)
                   ?? await db.Leads.AsTracking().FirstAsync(l => l.Id == existing[0].LeadId, ct);
            created = false;
        }
        else
        {
            agencyId ??= await ChannelAgencyAsync(tenantId, conversation.ConnectionId, ct);
            var firstAt = await FirstInboundAsync(conversation.Id, ct);

            lead = await CaptureAsync(tenantId, conversation.ContactPhone, conversation.ContactName,
                firstAt?.OccurredAt ?? conversation.CreatedAt, agencyId, ct);
            created = true;
        }

        foreach (var (code, value) in pending)
        {
            // On a lead somebody else created meanwhile, what an agent typed is not overwritten by
            // what the bot heard: only its empty fields are filled.
            if (!created && WhatsAppBotFieldValues.IsFilled(lead, new Dictionary<string, string>(), code)) continue;

            if (fieldValues.Write(lead, code, value, ctx) is { } refusal)
                logger.LogInformation("WhatsApp prospect: pending field {Field} not written ({Refusal}).", code, refusal);
        }

        var consentRecorded = await RecordConsentIfNoneAsync(lead, conversation.Id, ct);
        lead.RecordContactEstablished();
        conversation.AttachToLead(lead.Id);

        return (lead, consentRecorded);
    }

    private async Task<bool> RecordConsentIfNoneAsync(Lead lead, Guid conversationId, CancellationToken ct)
    {
        var hasAny = await db.LeadConsents.AnyAsync(
                         c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null, ct)
                     || db.LeadConsents.Local.Any(
                         c => c.LeadId == lead.Id && c.Type == ConsentType.WhatsAppContact && c.SubjectId == null);

        if (hasAny) return false;

        // The proof is the customer's FIRST message — the moment they asked to be talked to here,
        // not the moment the bot had heard enough to open a record.
        var first = await FirstInboundAsync(conversationId, ct);

        var consent = LeadConsent.Create(
            tenantId: lead.TenantId,
            leadId: lead.Id,
            type: ConsentType.WhatsAppContact,
            channel: ConsentChannel.WhatsApp,
            recordedBy: Guid.Empty,
            clock: clock,
            proofReference: $"Message WhatsApp entrant {first?.ProviderMessageId ?? conversationId.ToString()}");

        db.LeadConsents.Add(consent);
        lead.RaiseConsentRecordedEvent(consent.Id, ConsentType.WhatsAppContact.ToString());
        return true;
    }

    /// <summary>The customer's first message — stored, or still in this unit of work (the webhook's own).</summary>
    private async Task<WhatsAppMessage?> FirstInboundAsync(Guid conversationId, CancellationToken ct)
    {
        var stored = await db.WhatsAppMessages
            .Where(m => m.ConversationId == conversationId && m.Direction == WhatsAppDirection.Inbound)
            .OrderBy(m => m.OccurredAt)
            .FirstOrDefaultAsync(ct);

        var local = db.WhatsAppMessages.Local
            .Where(m => m.ConversationId == conversationId && m.Direction == WhatsAppDirection.Inbound)
            .MinBy(m => m.OccurredAt);

        return stored is null ? local : local is null || stored.OccurredAt <= local.OccurredAt ? stored : local;
    }

    private async Task<Guid?> ChannelAgencyAsync(Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        try
        {
            return (await integration.Messaging.GetChannelAsync(tenantId, connectionId, ct))?.AgencyId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A lead without an agency is still a lead; the escalation finds a manager another way.
            logger.LogWarning("WhatsApp prospect: the number's agency could not be read ({Reason}).", ex.GetType().Name);
            return null;
        }
    }
}
