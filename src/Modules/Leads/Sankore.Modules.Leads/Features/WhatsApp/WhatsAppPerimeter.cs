namespace Sankore.Modules.Leads.Features.WhatsApp;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Household;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;

/// <summary>A conversation with the lead it is filed on, if any.</summary>
internal sealed class WhatsAppScopedRow
{
    public WhatsAppConversation Conversation { get; init; } = default!;
    public Lead? Lead { get; init; }
}

/// <summary>
/// Which WhatsApp conversations the caller may see (WA-BE-19) — one definition, read by the inbox,
/// by the conversation detail, by the metrics and by the attach action, so a conversation listed
/// is a conversation that opens and a conversation that does not open is not counted.
///
/// <para>
/// An agent sees what they hold, what waits for them, and the conversations of the leads they
/// work. A supervisor (<c>WhatsApp.Supervise</c>) sees their team's plus the conversations nobody is
/// expected on — the queue a supervisor exists to place. The bypass roles of
/// <see cref="LeadAccessGuard"/> see the tenant.
/// </para>
/// </summary>
internal sealed class WhatsAppPerimeter(LeadsDbContext db, ICurrentUser me, IAdministrationModule admin)
{
    public async Task<IQueryable<WhatsAppScopedRow>> ScopeAsync(bool maySupervise, CancellationToken ct)
    {
        var rows =
            from c in db.WhatsAppConversations
            join l in db.Leads on c.LeadId equals l.Id into leads
            from l in leads.DefaultIfEmpty()
            select new WhatsAppScopedRow { Conversation = c, Lead = l };

        if (LeadAccessGuard.HasBypassRole(me)) return rows;

        if (maySupervise)
        {
            var team = (await admin.GetTeamAgentIdsAsync(me.TenantId, me.Id, ct)).Append(me.Id).ToList();

            return rows.Where(x =>
                (x.Conversation.OwnerUserId != null && team.Contains(x.Conversation.OwnerUserId.Value))
                || (x.Conversation.TargetAgentId != null && team.Contains(x.Conversation.TargetAgentId.Value))
                || (x.Lead != null && x.Lead.CurrentAssignedId != null && team.Contains(x.Lead.CurrentAssignedId.Value))
                || (x.Conversation.OwnerUserId == null && x.Conversation.TargetAgentId == null
                    && (x.Lead == null || x.Lead.CurrentAssignedId == null)));
        }

        var mine = me.Id;

        return rows.Where(x =>
            x.Conversation.OwnerUserId == mine
            || x.Conversation.TargetAgentId == mine
            || (x.Lead != null && (x.Lead.CurrentAssignedId == mine || x.Lead.OwnerId == mine)));
    }

    /// <summary>The conversation with its lead, or null when it does not exist OR is outside the perimeter — one answer for both.</summary>
    public async Task<WhatsAppScopedRow?> FindAsync(Guid conversationId, bool maySupervise, CancellationToken ct)
    {
        var scoped = await ScopeAsync(maySupervise, ct);
        return await scoped.FirstOrDefaultAsync(x => x.Conversation.Id == conversationId, ct);
    }

    /// <summary>
    /// Whether the caller is one of the people the conversation is about — its holder, the agent
    /// expected on it, or the lead's assignee or owner. Anybody else reading it is SUPERVISING it,
    /// and that is the consultation WA-BE-20 asks to audit.
    /// </summary>
    public static bool IsInvolved(Guid userId, WhatsAppConversation conversation, Lead? lead) =>
        conversation.OwnerUserId == userId
        || conversation.TargetAgentId == userId
        || lead?.CurrentAssignedId == userId
        || lead?.OwnerId == userId;
}
