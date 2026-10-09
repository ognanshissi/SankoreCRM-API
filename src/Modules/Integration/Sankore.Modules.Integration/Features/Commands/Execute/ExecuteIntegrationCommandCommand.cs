namespace Sankore.Modules.Integration.Features.Commands.Execute;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Executes ONE queued command: claims it, calls the external system, records the outcome
/// (INT-05).
///
/// <para>
/// Sent by the dispatcher job of INT-06, which is why the tenant is a FIELD and not read from an
/// ambient context: the job runs outside any HTTP request and processes many tenants in one pass.
/// Every read in the handler re-applies it explicitly.
/// </para>
///
/// <para>
/// No HTTP endpoint maps to this. A human does not execute a command — they replay or cancel
/// one, and the dispatcher decides when a replayed command runs. Exposing it would let a caller
/// bypass the per-customer ordering INT-06 enforces.
/// </para>
///
/// <para>
/// It is an <see cref="ICommand"/>, so it runs inside the pipeline's ambient transaction and is
/// audited. The audit row carries no payload: this record has none, and the aggregate's own
/// payload never leaves the module.
/// </para>
/// </summary>
internal sealed record ExecuteIntegrationCommandCommand(Guid CommandId, Guid TenantId)
    : IRequest<Result<ExecuteIntegrationCommandResult>>, ICommand;

/// <summary>
/// The outcome as DATA, not as a <see cref="Result"/> failure — and that distinction is load
/// bearing rather than stylistic.
///
/// <para>
/// <c>TransactionBehavior</c> commits the ambient scope only when the handler returns a
/// successful <see cref="Result"/>. A rejection returned as <c>Result.Fail</c> would therefore
/// roll back the very row that records the rejection, and the command would come back Pending
/// for ever — retrying a call the external system already refused on the merits. So every outcome
/// the handler managed to RECORD is a success of the handler, and the status, family and code
/// travel in this record for the dispatcher to log and count.
/// </para>
///
/// <para>
/// The only genuine failures are "no such command in that tenant" and an exception.
/// </para>
/// </summary>
internal sealed record ExecuteIntegrationCommandResult(
    Guid CommandId, string Status, string? ErrorFamily, string? ErrorCode);
