namespace Sankore.Modules.Integration.Features.KycLimits;

using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Recognises the violation of <c>ux_integration_kyc_limit_alert_period</c> — the unique index on
/// <c>(tenant_id, crm_customer_id, limit_kind, severity, period)</c> that IS the once-per-customer-
/// per-month rule of INT-22.
///
/// <para>
/// The watch inserts the ledger row and publishes only if the insert succeeded. It does not read
/// first: the job runs nightly per tenant and a retry after a partial failure would pass a
/// read-then-write check twice — which for an operator means a second upgrade task for a customer
/// who already has one. The index is the only check a retry cannot slip past.
/// </para>
///
/// <para>
/// Same device, and the same message fallback, as M01's <c>GroupUniqueViolation</c>: the
/// constraint name is matched on <see cref="PostgresException"/> where Npgsql reports it, and on
/// the exception message otherwise, so the branch stays reachable under a provider that surfaces
/// no <see cref="PostgresException"/> at all.
/// </para>
/// </summary>
internal static class KycLimitAlertUniqueViolation
{
    /// <summary>PostgreSQL SQLSTATE for "unique_violation".</summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// The index name as the migration declares it. Referenced and never retyped: a typo here
    /// would turn the dedup into an unhandled 500 — or, worse, into a swallowed exception that
    /// also hides a genuine write failure.
    /// </summary>
    internal const string PeriodIndex = "ux_integration_kyc_limit_alert_period";

    internal static bool IsAlreadyRaised(this DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg)
        {
            return pg.ConstraintName is null
                   || pg.ConstraintName.Contains(PeriodIndex, StringComparison.OrdinalIgnoreCase);
        }

        return Mentions(ex.Message) || Mentions(ex.InnerException?.Message);
    }

    private static bool Mentions(string? message)
        => message is not null && message.Contains(PeriodIndex, StringComparison.OrdinalIgnoreCase);
}
