namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// The consolidated core-banking view of one customer — the read model Customer 360 displays
/// without calling the CBS (INT-21).
///
/// <para>
/// Named <c>CbsSnapshot</c> in code and <c>cbs_customer_snapshot</c> in the schema: the
/// <c>cbs_</c> prefix marks the tables that belong to the core-banking family only, as ASS-01
/// requires, and the class name stays distinct from the PublicApi DTO
/// <see cref="PublicApi.CbsCustomerSnapshot"/> so Swashbuckle — which keys components by simple
/// name — cannot collide on them.
/// </para>
///
/// <para>
/// Accounts and loans are jsonb and not child tables, deliberately. Nothing queries across
/// customers' accounts: this object is read whole, by primary key, to render one screen. Child
/// tables would buy joins nobody performs and cost a delete-and-reinsert on every sync.
/// </para>
///
/// <para>
/// Composite primary key <c>(tenant_id, crm_customer_id)</c>, as specified — one snapshot per
/// customer, enforced by the key rather than by the writer remembering to check.
/// </para>
/// </summary>
public sealed class CbsSnapshot : AggregateRoot
{
    public Guid CrmCustomerId { get; private set; }

    /// <summary>Which connection produced it. Not part of the key: a tenant has one CBS.</summary>
    public Guid ConnectionId { get; private set; }

    /// <summary>Serialised <see cref="CbsAccount"/> list, with CRM product codes already mapped back.</summary>
    public string AccountsJson { get; private set; } = "[]";

    public string LoansJson { get; private set; } = "[]";

    public decimal TotalBalance { get; private set; }

    /// <summary>Everything that moved over the tenant's flow window — what the ceiling applies to.</summary>
    public decimal MonthlyFlow { get; private set; }

    /// <summary>
    /// The tier the CBS believes. Null when the CBS does not expose one. Compared with M02's own
    /// tier, and a disagreement is reported rather than corrected: which side is right is a
    /// compliance decision.
    /// </summary>
    public KycLevel? KycLevelInCbs { get; private set; }

    public DateTimeOffset SnapshotAt { get; private set; }

    public uint Version { get; private set; }

    private CbsSnapshot() { }

    public static CbsSnapshot Create(
        Guid tenantId, Guid crmCustomerId, Guid connectionId, TimeProvider clock)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");

        return new CbsSnapshot
        {
            TenantId = tenantId,
            CrmCustomerId = crmCustomerId,
            ConnectionId = connectionId,
            SnapshotAt = clock.GetUtcNow(),
        };
    }

    public void Update(
        string accountsJson,
        string loansJson,
        decimal totalBalance,
        decimal monthlyFlow,
        KycLevel? kycLevelInCbs,
        TimeProvider clock)
    {
        AccountsJson = accountsJson;
        LoansJson = loansJson;
        TotalBalance = totalBalance;
        MonthlyFlow = monthlyFlow;
        KycLevelInCbs = kycLevelInCbs;
        SnapshotAt = clock.GetUtcNow();
    }
}
