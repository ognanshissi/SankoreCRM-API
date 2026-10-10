namespace Sankore.Modules.Integration.Infrastructure.CallLog;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// The append. One method, so that <see cref="CallJournal"/> can be unit-tested against a double
/// and the persistence rules below can be tested once, on their own, against a real context.
/// </summary>
internal interface ICallLogStore
{
    Task AppendAsync(IntegrationCallLog row, CancellationToken ct);
}

/// <summary>
/// Writes one journal row on its own unit of work, outside the caller's transaction.
///
/// <para>
/// <b>Why the suppressed scope.</b> An adapter is almost always called from a handler or a
/// dispatcher job, and <c>TransactionBehavior</c> wraps every <c>ICommand</c> in an ambient
/// <see cref="TransactionScope"/> that it completes <i>only</i> when the handler returns a
/// successful <c>Result</c>. Enlisting the journal in that scope would delete exactly the rows
/// worth keeping: a <c>Functional</c> rejection makes the handler fail, the scope rolls back, and
/// the proof that the CBS refused the write rolls back with it. The journal is a record of what
/// was attempted, not of what succeeded, so it must commit independently.
/// </para>
///
/// <para>
/// <b>Why a child scope.</b> The caller's <see cref="IntegrationDbContext"/> is carrying that
/// handler's pending changes; calling <c>SaveChangesAsync</c> on it would flush them early, at a
/// point no handler chose. A fresh scope gives a context with an empty change tracker — the same
/// reasoning as <c>SqlAuditWriter</c>'s <c>IDbContextFactory</c>, expressed with the registration
/// this module already has.
/// </para>
///
/// <para>
/// The row carries its own <c>TenantId</c>, set from the adapter's <see cref="CallContext"/>, so
/// the insert does not depend on the child scope resolving an ambient tenant — which it could
/// not do reliably anyway, since an adapter may be called from a Hangfire job with no HTTP
/// context.
/// </para>
/// </summary>
internal sealed class CallLogStore(IServiceScopeFactory scopeFactory) : ICallLogStore
{
    public async Task AppendAsync(IntegrationCallLog row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        using var suppressed = new TransactionScope(
            TransactionScopeOption.Suppress,
            TransactionScopeAsyncFlowOption.Enabled);

        using var scope = scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();

        db.CallLogs.Add(row);
        await db.SaveChangesAsync(ct);

        suppressed.Complete();
    }
}
