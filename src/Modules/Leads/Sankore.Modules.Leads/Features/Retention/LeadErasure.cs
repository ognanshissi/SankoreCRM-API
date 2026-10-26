namespace Sankore.Modules.Leads.Features.Retention;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>
/// What erasing a lead touches, in ONE place: the nightly retention job (closed leads past their
/// period) and an erasure a person requested (approved by a second one) erase the same things.
///
/// <para>
/// The lead and its household members, its qualification answers, consents and FATCA declarations,
/// its WhatsApp threads (messages and bot turns), and what agents wrote about it — activities,
/// reminders, appointments. The rows stay, emptied: what they say about the funnel is the
/// statistics an institution measures itself on. See <c>Lead.Anonymize</c> for the lead itself.
/// </para>
///
/// <para>
/// The caller loads the leads TRACKED, with their household members, and saves once: every table
/// or none — a lead whose name is gone and whose spouse's is not would never be revisited.
/// </para>
/// </summary>
internal static class LeadErasure
{
    public static async Task AnonymizeAsync(
        LeadsDbContext db, Guid tenantId, IReadOnlyList<Lead> leads, DateTimeOffset now, CancellationToken ct)
    {
        if (leads.Count == 0) return;

        // A List: on .NET 10 an array's Contains binds to the span extension and does not translate.
        var leadIds = leads.Select(l => l.Id).ToList();

        // Loaded per table rather than per lead: the retention job runs over a backlog.
        var responses = await db.QualificationResponses
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(r => r.TenantId == tenantId && r.LeadId != null && leadIds.Contains(r.LeadId.Value) && !r.IsPurged)
            .ToListAsync(ct);

        var consents = await db.LeadConsents
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId && leadIds.Contains(c.LeadId))
            .ToListAsync(ct);

        var declarations = await db.FatcaDeclarations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(d => d.TenantId == tenantId && leadIds.Contains(d.LeadId))
            .ToListAsync(ct);

        // WhatsApp (WA lot 1): the customer's number, profile name and every word exchanged.
        var conversations = await db.WhatsAppConversations
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(c => c.TenantId == tenantId && c.LeadId != null && leadIds.Contains(c.LeadId.Value))
            .ToListAsync(ct);

        var conversationIds = conversations.Select(c => c.Id).ToList();

        var messages = conversationIds.Count == 0
            ? []
            : await db.WhatsAppMessages
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(m => m.TenantId == tenantId && conversationIds.Contains(m.ConversationId))
                .ToListAsync(ct);

        // The bot's turns (WA lot 3): the transcript, the answer and the values the bot wrote.
        var botTurns = conversationIds.Count == 0
            ? []
            : await db.WhatsAppBotTurns
                .IgnoreQueryFilters()
                .AsTracking()
                .Where(t => t.TenantId == tenantId && conversationIds.Contains(t.ConversationId))
                .ToListAsync(ct);

        // What agents wrote about the person (2026-10-24): before, the lead's name went and the
        // notes « Rappelé Mme Konan, divorcée, 2 enfants » stayed.
        var activities = await db.LeadActivities
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(a => a.TenantId == tenantId && leadIds.Contains(a.LeadId))
            .ToListAsync(ct);

        var reminders = await db.LeadReminders
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(r => r.TenantId == tenantId && leadIds.Contains(r.LeadId))
            .ToListAsync(ct);

        var appointments = await db.Appointments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(a => a.TenantId == tenantId && a.LeadId != null && leadIds.Contains(a.LeadId.Value))
            .ToListAsync(ct);

        foreach (var conversation in conversations) conversation.Anonymize(now);
        foreach (var message in messages) message.Anonymize();
        foreach (var turn in botTurns) turn.Anonymize();
        await WhatsApp.Complaints.WhatsAppComplaintIntakes.AnonymizeAsync(db, tenantId, conversationIds, now, ct);

        foreach (var lead in leads)
        {
            lead.Anonymize(now);

            // Every member, removed ones included: a spouse whose link was closed before the lead
            // was is still a person whose name is in this table.
            foreach (var member in lead.HouseholdMembers)
                member.Anonymize();
        }

        // Purge and not just blank: IsPurged says the row's emptiness is deliberate.
        foreach (var response in responses) response.Purge();
        foreach (var consent in consents) consent.Anonymize();
        foreach (var declaration in declarations) declaration.Anonymize();
        foreach (var activity in activities) activity.Anonymize();
        foreach (var reminder in reminders) reminder.Anonymize();
        foreach (var appointment in appointments) appointment.Anonymize();
    }
}
