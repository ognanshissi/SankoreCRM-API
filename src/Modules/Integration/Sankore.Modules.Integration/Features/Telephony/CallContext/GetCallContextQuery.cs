namespace Sankore.Modules.Integration.Features.Telephony.CallContext;

using MediatR;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// What the screen that opens while a telephone is ringing needs to know (TEL-B-10).
///
/// <para>
/// <b>A read and not a push payload, and that split is the whole of B-10's privacy story.</b> The
/// push carries a call id and nothing else; this read turns it into a name — under the caller's
/// permission and inside the caller's agency perimeter, neither of which a websocket frame can
/// apply.
/// </para>
///
/// <para>
/// A query: no <c>ICommand</c>, so neither <c>TransactionBehavior</c> nor <c>AuditBehavior</c>
/// wraps it. Opening a screen pop is not an event worth an audit row, and the reveal that WOULD
/// be — a number in clear — never happens here: the context names people, never numbers.
/// </para>
/// </summary>
internal sealed record GetCallContextQuery(Guid CallId)
    : IRequest<Result<TelephonyCallContext>>;
