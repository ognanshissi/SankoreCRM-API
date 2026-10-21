namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>How a handover to a person ended.</summary>
public enum WhatsAppHandoverEnd
{
    /// <summary>An agent took the conversation.</summary>
    TakenOver,

    /// <summary>Closed before anybody took it.</summary>
    Closed,

    /// <summary>Given back to the bot before anybody took it.</summary>
    ReturnedToBot,
}

/// <summary>
/// One time a conversation waited for a person (WA-BE-22): why, since when, who took it and when.
///
/// <para>
/// <b>A history, because the conversation only keeps the present.</b> <c>PendingSince</c> and
/// <c>TakenOverAt</c> are overwritten at the next escalation, so « délai de prise en charge » and
/// « taux d'escalade » over a month cannot be read off the conversations: they are read off these
/// rows. Identifiers, instants and a reason code — nothing a customer said.
/// </para>
/// </summary>
public sealed class WhatsAppHandover : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid? LeadId { get; private set; }

    /// <summary>The escalation code — <c>CUSTOMER_REPLIED</c>, a <c>BOT_*</c> rule or the bot's own reason.</summary>
    public string Reason { get; private set; } = default!;

    /// <summary>Whether the bot was holding the conversation when it went to a person.</summary>
    public bool FromBot { get; private set; }

    public DateTimeOffset EscalatedAt { get; private set; }
    public DateTimeOffset? TakeoverDeadline { get; private set; }
    public DateTimeOffset? BreachedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }
    public WhatsAppHandoverEnd? EndedBy { get; private set; }
    public Guid? TakenOverBy { get; private set; }

    private WhatsAppHandover() { } // EF Core

    public static WhatsAppHandover Open(
        Guid tenantId, Guid conversationId, Guid? leadId, string reason, bool fromBot,
        DateTimeOffset? takeoverDeadline, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        ConversationId = conversationId,
        LeadId = leadId,
        Reason = reason.Length <= 60 ? reason : reason[..60],
        FromBot = fromBot,
        EscalatedAt = now,
        TakeoverDeadline = takeoverDeadline,
    };

    public bool IsOpen => EndedAt is null;

    public void End(WhatsAppHandoverEnd how, Guid? takenOverBy, DateTimeOffset now)
    {
        if (!IsOpen) return;
        EndedAt = now;
        EndedBy = how;
        TakenOverBy = how == WhatsAppHandoverEnd.TakenOver ? takenOverBy : null;
    }

    public void MarkBreached(DateTimeOffset now) => BreachedAt ??= now;

    /// <summary>The conversation was filed on a lead while it waited.</summary>
    public void AttachToLead(Guid leadId) => LeadId = leadId;

    /// <summary>A reassignment of a still-waiting conversation restarts the SLA for the new agent.</summary>
    public void MoveDeadline(DateTimeOffset? deadline) => TakeoverDeadline = deadline;
}
