namespace Sankore.Modules.Integration.Features.Snapshot;

/// <summary>
/// Refreshes <c>cbs_customer_snapshot</c> for ONE customer (INT-21).
///
/// <para>
/// The seam between the synchronisation (INT-20) and this read model. The synchronisation decides
/// WHICH customers are due and in what order; everything about WHAT a snapshot contains — which
/// ports are called, how a CBS code becomes a CRM code, how the totals are added up, when a KYC
/// divergence is reported — lives behind this one method. A synchronisation that knew any of that
/// would have to be edited every time the read model gains a column.
/// </para>
///
/// <para>
/// <b>Safe to call repeatedly for the same customer.</b> Delivery of the synchronisation is
/// at-least-once, so a second call for the same customer must refresh the one row rather than add
/// a second — which the composite key <c>(tenant_id, crm_customer_id)</c> guarantees and the
/// upsert here respects.
/// </para>
///
/// <para>
/// It returns nothing on purpose. Every outcome that is not a refreshed row — the customer is
/// unknown to the CBS, the adapter is unreachable, the installation cannot read accounts — is an
/// ordinary state of a background projection, not something a caller can act on differently. The
/// journal (<c>integration_call_log</c>) is where an operator looks for what the CBS answered, and
/// a failed projection deliberately leaves the PREVIOUS snapshot standing: a figure from last
/// night, shown with its <c>SnapshotAt</c>, beats no figure at all.
/// </para>
/// </summary>
internal interface ICbsSnapshotProjector
{
    Task ProjectAsync(Guid tenantId, Guid connectionId, Guid crmCustomerId, CancellationToken ct);
}
