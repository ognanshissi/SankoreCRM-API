namespace Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An operator closes a reconciliation gap by hand (INT-34, criterion 5).
///
/// <para>
/// Audited with its author, which is what the criterion asks for: it is an
/// <see cref="ICommand"/> (so <c>AuditBehavior</c> records it under <c>ICurrentUser.Id</c>) and an
/// <see cref="IResourceCommand"/> (so the audit row is filterable by the gap it closed). The
/// author is never a field of this record — a caller-supplied actor is an audit trail anybody can
/// write somebody else's name into, and the aggregate stamps <c>ResolvedBy</c> from the
/// authenticated identity.
/// </para>
///
/// <para>
/// <b>The note is mandatory</b>, and not merely as a form nicety. The aggregate enforces it and
/// explains why: "an untraceable resolution of a compliance finding is the one failure mode this
/// table exists to prevent". The validator refuses a blank one before the handler runs so the
/// caller gets a 422 naming the field rather than the aggregate's generic
/// <c>INTEGRATION_PAYLOAD_INVALID</c>.
/// </para>
///
/// <para>
/// <b>Resolving changes nothing on either side.</b> It records that a human looked at the
/// divergence and dealt with it; it does not write to the CBS, does not re-point a reference and
/// does not move a KYC tier. INT-34 reports, the operator acts through the ordinary endpoints,
/// and the next night's comparison is what confirms the divergence is gone — a gap closed here
/// that is still true tomorrow is re-opened as a NEW row with tomorrow's <c>DetectedAt</c>, which
/// is exactly the behaviour the filtered unique index on the OPEN rows is designed for.
/// </para>
/// </summary>
internal sealed record ResolveReconciliationGapCommand(Guid GapId, string Note)
    : IRequest<Result<ResolveReconciliationGapResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationReconciliationGap";

    public string? ResourceId => GapId.ToString();
}

/// <param name="ResolvedBy">
/// The authenticated operator the aggregate stamped. Echoed back so the screen can show who
/// closed it without a second read.
/// </param>
internal sealed record ResolveReconciliationGapResult(
    Guid GapId,
    string GapType,
    string Resolution,
    Guid? ResolvedBy,
    DateTimeOffset? ResolvedAt);
