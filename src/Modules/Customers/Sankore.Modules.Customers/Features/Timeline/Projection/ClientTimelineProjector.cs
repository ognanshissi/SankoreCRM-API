namespace Sankore.Modules.Customers.Features.Timeline.Projection;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// Writes the client timeline read model.
///
/// ── Why the model never changes when a new module arrives ────────────────────
/// A timeline entry is deliberately a FLAT, MODULE-AGNOSTIC fact:
///     (SourceModule, EntryType, OccurredAt, Summary, ReferenceType, ReferenceId, DedupKey)
/// There is no discriminated payload, no per-module column and, above all, no physical
/// foreign key towards another schema: <c>ReferenceType</c>/<c>ReferenceId</c> are an
/// OPAQUE back-reference that the front-end turns into a deep link. So when M02 (KYC),
/// M03 (Savings), M04 (Credit) or M08 (Notifications) start emitting events, integrating
/// them means adding ONE consumer per event in <c>Features/Timeline/Consumers/</c> plus a
/// constant in <see cref="TimelineSourceModules"/> — no migration, no change to this class,
/// no change to <c>GET clients/{clientId}/timeline</c>. That is the whole point of routing
/// every producer through <see cref="IClientTimelineProjector"/> instead of letting each
/// module insert its own rows.
///
/// ── Idempotency (double guard) ───────────────────────────────────────────────
/// Delivery is at-least-once (outbox + broker), so the same fact WILL arrive twice.
///   1. Consumers ask <c>IInboxGuard</c> first: a redelivered message id exits silently.
///   2. This projector is the second, independent guard, for the cases the inbox cannot
///      cover — a fact republished under a fresh message id, a manual replay, two producers
///      projecting the same underlying fact. It pre-checks <c>(TenantId, DedupKey)</c> and
///      still catches the unique-index violation, because between the check and the insert
///      a concurrent consumer may have won the race.
///
/// ── No sensitive data ────────────────────────────────────────────────────────
/// Every summary goes through <see cref="TimelineSummaryGuard"/>. See that class for why.
/// </summary>
internal sealed class ClientTimelineProjector(
    CustomersDbContext db,
    ILogger<ClientTimelineProjector> logger) : IClientTimelineProjector
{
    public async Task AppendAsync(
        Guid tenantId,
        Guid clientId,
        string sourceModule,
        string entryType,
        DateTimeOffset occurredAt,
        string summary,
        string? referenceType,
        string? referenceId,
        string dedupKey,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModule);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryType);
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);

        // Callers run outside an HTTP request (MassTransit consumers, Hangfire jobs):
        // the ambient tenant of the DbContext is not necessarily this tenant.
        var alreadyProjected = await db.ClientTimelineEntries
            .IgnoreQueryFilters()
            .AnyAsync(e => e.TenantId == tenantId && e.DedupKey == dedupKey, ct);

        if (alreadyProjected)
        {
            logger.LogDebug(
                "Timeline entry {DedupKey} already projected for tenant {TenantId} — skipped.",
                dedupKey, tenantId);
            return;
        }

        if (TimelineSummaryGuard.LooksSensitive(summary))
        {
            // A producer built a summary out of unstructured data. The value is redacted
            // below; this warning is what makes the offending producer findable.
            logger.LogWarning(
                "Timeline summary for entry type {EntryType} from module {SourceModule} matched a "
                + "sensitive pattern and was redacted before persistence (tenant {TenantId}).",
                entryType, sourceModule, tenantId);
        }

        var entry = ClientTimelineEntry.Create(
            tenantId: tenantId,
            clientId: clientId,
            sourceModule: sourceModule,
            entryType: entryType,
            occurredAt: occurredAt,
            summary: TimelineSummaryGuard.Sanitize(summary),
            referenceType: referenceType,
            referenceId: referenceId,
            dedupKey: Truncate(dedupKey, 200));

        db.ClientTimelineEntries.Add(entry);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Second guard fired: a concurrent consumer inserted the same fact between the
            // pre-check and this insert. Nothing to repair — the entry exists. Detach the
            // rejected entity so a caller sharing this DbContext can keep working.
            db.Entry(entry).State = EntityState.Detached;

            logger.LogDebug(
                "Concurrent projection of timeline entry {DedupKey} (tenant {TenantId}) — ignored.",
                dedupKey, tenantId);
        }
    }

    /// <summary>
    /// Provider-agnostic detection of the <c>ux_client_timeline_dedup</c> violation.
    /// Npgsql surfaces SQLSTATE 23505 on <c>PostgresException.SqlState</c>; the
    /// in-memory provider used by the tests raises a plain
    /// <see cref="DbUpdateException"/>, so the message is inspected as a fallback
    /// rather than referencing Npgsql from a feature folder.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var sqlState = e.GetType().GetProperty("SqlState")?.GetValue(e) as string;
            if (sqlState == "23505") return true;

            if (e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("ux_client_timeline_dedup", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
