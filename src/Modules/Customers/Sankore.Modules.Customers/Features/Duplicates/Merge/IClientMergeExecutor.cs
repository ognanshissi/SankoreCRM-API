namespace Sankore.Modules.Customers.Features.Duplicates.Merge;

using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>
/// Executes an APPROVED merge request: moves everything the absorbed client owns onto the survivor,
/// applies the per-field arbitration, and closes the absorbed record.
/// <para>
/// Separated from the approval handler because it is the only part worth testing exhaustively and
/// because a future asynchronous execution path (a very large client, a workflow callback) would
/// reuse it unchanged. It never calls <c>SaveChangesAsync</c>: the calling handler is an
/// <c>ICommand</c>, so <c>TransactionBehavior</c> owns the transaction and one save covers the
/// reassignments, the status change and the outbox rows.
/// </para>
/// <para>
/// Public rather than internal so it can be substituted in tests: Castle DynamicProxy cannot proxy
/// an internal interface unless the module assembly opts DynamicProxyGenAssembly2 in, and that is a
/// solution-wide decision, not this zone's to make. The implementation stays internal.
/// </para>
/// </summary>
public interface IClientMergeExecutor
{
    Task<Result> ExecuteAsync(ClientMergeRequest request, Guid actorUserId, CancellationToken ct);
}
