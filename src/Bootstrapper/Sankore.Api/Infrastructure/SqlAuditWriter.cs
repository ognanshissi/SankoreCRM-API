namespace Sankore.Api.Infrastructure;

using Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// Minimal IAuditWriter implementation for local development: logs audit
/// entries through ILogger. In staging/production this should be replaced
/// by an implementation that writes to an append-only, tamper-evident
/// store (a dedicated "audit" schema with no UPDATE/DELETE grants, or an
/// external service) — the BCEAO requirement is immutability, which a
/// simple table with app-level DELETE/UPDATE revoked already satisfies for
/// an MVP.
/// </summary>
public sealed class SqlAuditWriter(ILogger<SqlAuditWriter> logger) : IAuditWriter
{
    public Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        logger.LogInformation(
            "AUDIT | {Timestamp:o} | tenant={TenantId} user={UserId} action={Action} outcome={Outcome} error={Error}",
            entry.Timestamp, entry.TenantId, entry.UserId, entry.Action, entry.Outcome, entry.ErrorDetail);

        // TODO (production): INSERT into an append-only `audit.entries` table
        // via a dedicated AuditDbContext, separate from any module's schema,
        // with database-level REVOKE UPDATE/DELETE for the app role.
        return Task.CompletedTask;
    }
}
