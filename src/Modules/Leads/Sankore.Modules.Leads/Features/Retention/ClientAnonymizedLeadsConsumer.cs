namespace Sankore.Modules.Leads.Features.Retention;

using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi.Events;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi.Events;
using Sankore.Shared.Infrastructure.Messaging;

/// <summary>
/// A client was anonymised (retention, or a person's request): this module's copies of the person
/// go too. Before, nothing here listened — the client's name was gone and the lead it came from,
/// its WhatsApp threads and appointments still said it (data-purge lot 2).
///
/// <para>
/// The leads of that person — linked or converted — are erased through <see cref="LeadErasure"/>
/// and each publishes <see cref="LeadErasedEvent"/>, so their calls follow; the threads and
/// appointments filed on the client directly are anonymised here. Idempotent: an anonymised lead
/// is not selected again, and anonymising an emptied row changes nothing.
/// </para>
/// </summary>
public sealed class ClientAnonymizedLeadsConsumer(
    LeadsDbContext db,
    TimeProvider clock,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher,
    ILogger<ClientAnonymizedLeadsConsumer> logger)
    : IConsumer<ClientAnonymizedEvent>
{
    public async Task Consume(ConsumeContext<ClientAnonymizedEvent> context)
    {
        var evt = context.Message;
        var ct = context.CancellationToken;
        if (evt.TenantId == Guid.Empty || evt.ClientId == Guid.Empty) return;

        var now = clock.GetUtcNow();

        var leads = await db.Leads
            .IgnoreQueryFilters()
            .AsTracking()
            .Include(l => l.HouseholdMembers)
            .Where(l => l.TenantId == evt.TenantId && !l.IsAnonymized
                     && (l.CustomerId == evt.ClientId || l.ConvertedToCustomerId == evt.ClientId))
            .ToListAsync(ct);

        await LeadErasure.AnonymizeAsync(db, evt.TenantId, leads, now, ct);
        foreach (var lead in leads)
            await publisher.PublishAsync(new LeadErasedEvent(evt.TenantId, lead.Id, now), ct);

        var conversations = await db.WhatsAppConversations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == evt.TenantId && c.ClientId == evt.ClientId)
            .ToListAsync(ct);
        var conversationIds = conversations.Select(c => c.Id).ToList();

        if (conversationIds.Count > 0)
        {
            foreach (var conversation in conversations) conversation.Anonymize(now);

            var messages = await db.WhatsAppMessages
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(m => m.TenantId == evt.TenantId && conversationIds.Contains(m.ConversationId))
                .ToListAsync(ct);
            foreach (var message in messages) message.Anonymize();

            var turns = await db.WhatsAppBotTurns
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(t => t.TenantId == evt.TenantId && conversationIds.Contains(t.ConversationId))
                .ToListAsync(ct);
            foreach (var turn in turns) turn.Anonymize();

            await WhatsApp.Complaints.WhatsAppComplaintIntakes.AnonymizeAsync(db, evt.TenantId, conversationIds, now, ct);
        }

        var appointments = await db.Appointments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(a => a.TenantId == evt.TenantId && a.ClientId == evt.ClientId)
            .ToListAsync(ct);
        foreach (var appointment in appointments) appointment.Anonymize();

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Client {ClientId} of tenant {TenantId} anonymised: {Leads} lead(s), {Threads} WhatsApp thread(s), "
            + "{Appointments} appointment(s) erased here.",
            evt.ClientId, evt.TenantId, leads.Count, conversations.Count, appointments.Count);
    }
}
