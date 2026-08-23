using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Api.Infrastructure.Audit;
using Sankore.Shared.Infrastructure.Behaviors;

using AuditEntryDto = Sankore.Shared.Infrastructure.Behaviors.AuditEntry;

namespace Sankore.Api.Infrastructure;

/// <summary>
/// Persists audit entries into the append-only <c>audit.entries</c> table.
///
/// The write runs inside a <see cref="TransactionScopeOption.Suppress"/> scope
/// so it commits independently of the calling command's business transaction.
/// This guarantees that audit entries are written even when the business
/// transaction rolls back (e.g. on a <c>Result.Fail</c> outcome).
///
/// A fresh <see cref="AuditDbContext"/> is created per call via
/// <see cref="IDbContextFactory{TContext}"/> so it is never enrolled in the
/// ambient <see cref="TransactionScope"/> opened by <c>TransactionBehavior</c>.
/// </summary>
public sealed class SqlAuditWriter(
    IDbContextFactory<AuditDbContext> dbContextFactory,
    IHttpContextAccessor httpContextAccessor
) : IAuditWriter
{
    public async Task WriteAsync(AuditEntryDto entry, CancellationToken ct)
    {
        using var suppressedScope = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);

        var context = httpContextAccessor.HttpContext;

        var entity = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = entry.Timestamp,
            UserId = entry.UserId,
            TenantId = entry.TenantId,
            Action = entry.Action,
            PayloadJson = entry.PayloadJson,
            Outcome = entry.Outcome,
            ErrorDetail = entry.ErrorDetail,
            ResourceType = entry.ResourceType,
            ResourceId = entry.ResourceId,
            IpAddress = context?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = context?.Request.Headers.UserAgent.ToString(),
            CorrelationId = context?.TraceIdentifier,
        };

        db.Entries.Add(entity);
        await db.SaveChangesAsync(ct);

        suppressedScope.Complete();
    }
}
