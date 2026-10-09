namespace Sankore.Modules.Integration.Domain;

using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>Which simplified-KYC ceiling an alert is about (INT-22).</summary>
public enum KycLimitKind
{
    /// <summary>Total balance across the customer's accounts.</summary>
    Balance,

    /// <summary>Money moved over the tenant's flow window.</summary>
    Flow
}

/// <summary>How close to the ceiling the customer was when the alert was raised.</summary>
public enum KycLimitSeverity
{
    /// <summary>At or past the tenant's alert threshold, 80 % by default.</summary>
    Approaching,

    /// <summary>Past the ceiling itself.</summary>
    Exceeded
}

/// <summary>
/// One alert already raised for one customer, one ceiling, one month (INT-22).
///
/// <para>
/// <b>This table is an addition to the schema the specification lists</b>, and INT-22's own
/// criterion is what requires it: "il publie KycLimitApproaching <i>une seule fois par client et
/// par mois</i>". A nightly job has no other way to know what yesterday's run already said. The
/// alternatives were worse — a column on <c>cbs_customer_snapshot</c> would deviate from a table
/// the specification defines field by field, and leaning on M08's idempotency key would dedupe
/// the e-mail while still publishing the event that makes M02 open a second upgrade task.
/// </para>
///
/// <para>
/// The row is the LEDGER, not the alert: it exists so the event is not published twice, and it
/// carries the observed figures only so an operator asking "why was I told this in March" has an
/// answer. Nothing reads it to make a decision.
/// </para>
/// </summary>
public sealed class KycLimitAlert : AggregateRoot
{
    public Guid Id { get; private set; }

    /// <summary>Opaque reference to the customer record of M01.</summary>
    public Guid CrmCustomerId { get; private set; }

    public KycLimitKind LimitKind { get; private set; }

    public KycLimitSeverity Severity { get; private set; }

    /// <summary>
    /// The calendar month the alert belongs to, as <c>yyyy-MM</c>. A string and not a date: it is
    /// a bucket, and a date would invite a range query that treats the 1st of the month as
    /// different from the 20th when the dedup rule says they are the same.
    /// </summary>
    public string Period { get; private set; } = string.Empty;

    public decimal Observed { get; private set; }

    public decimal Ceiling { get; private set; }

    public DateTimeOffset RaisedAt { get; private set; }

    private KycLimitAlert() { }

    public static KycLimitAlert Raise(
        Guid tenantId,
        Guid crmCustomerId,
        KycLimitKind limitKind,
        KycLimitSeverity severity,
        YearMonth period,
        decimal observed,
        decimal ceiling,
        TimeProvider clock,
        Guid? id = null)
    {
        if (tenantId == Guid.Empty) throw new DomainException("TenantId is required.");
        if (crmCustomerId == Guid.Empty) throw new DomainException("CrmCustomerId is required.");

        return new KycLimitAlert
        {
            Id = id ?? Guid.NewGuid(),
            TenantId = tenantId,
            CrmCustomerId = crmCustomerId,
            LimitKind = limitKind,
            Severity = severity,
            Period = period.ToString(),
            Observed = observed,
            Ceiling = ceiling,
            RaisedAt = clock.GetUtcNow(),
        };
    }
}
