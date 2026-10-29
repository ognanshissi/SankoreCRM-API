namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// Something a partner reports about one of the leads it sent (webhooks lot 6) — « rendez-vous
/// pris », « dossier complété » — pushed to the source's events endpoint. Matched to the lead by
/// the partner's external id or by phone, then announced to the Workflow module as an
/// <c>ExternalEvent</c> trigger: what it causes is whatever the tenant configured there.
///
/// <para>
/// One row per (source, partner event id): a partner retrying the same event finds it and
/// starts nothing twice. The payload is kept like an ingestion's, and purged on the same delay.
/// </para>
/// </summary>
public sealed class LeadExternalEvent : ITenant
{
    public const int EventIdMaxLength = 200;
    public const int TypeMaxLength = 100;

    private LeadExternalEvent() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>The partner's own id for the event — the idempotency key.</summary>
    public string ExternalEventId { get; private set; } = default!;

    /// <summary>Lower-case, dotted: the <c>EventName</c> a workflow trigger matches.</summary>
    public string EventType { get; private set; } = default!;

    /// <summary>Null when no lead matched: the event is kept, nothing is triggered.</summary>
    public Guid? LeadId { get; private set; }

    /// <summary><c>externalId</c> or <c>phone</c>; null when unmatched.</summary>
    public string? MatchedBy { get; private set; }

    public string? PayloadJson { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }

    public bool IsMatched => LeadId is not null;

    public static LeadExternalEvent Record(
        Guid tenantId, Guid sourceId, string externalEventId, string eventType,
        Guid? leadId, string? matchedBy, string payloadJson, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SourceId = sourceId,
            ExternalEventId = externalEventId,
            EventType = eventType,
            LeadId = leadId,
            MatchedBy = leadId is null ? null : matchedBy,
            PayloadJson = payloadJson,
            ReceivedAt = now,
        };
}
