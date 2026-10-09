namespace Sankore.Modules.Integration.Features.Commands.ReplayCommand;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An operator puts a rejected command back in the queue (INT-05, criterion 5).
///
/// <para>
/// Only from <c>Rejected</c> — the transition table says so and the aggregate enforces it — and
/// the attempt counter is reset by <c>IntegrationCommand.Replay</c>: somebody who has just fixed
/// a missing mapping is starting a new story, and leaving the count at eight would reject the
/// command on its first new failure.
/// </para>
///
/// <para>
/// Audited with its author, which is what criterion 5 asks for: it is an
/// <see cref="ICommand"/> (so <c>AuditBehavior</c> records it under <c>ICurrentUser.Id</c>) and
/// an <see cref="IResourceCommand"/> (so the row is filterable by the command it replayed). The
/// author is never a field of this record — a caller-supplied actor is an audit trail anybody can
/// write somebody else's name into.
/// </para>
///
/// <para>
/// <b>A replay carries NO payload, and must never be given one.</b> It re-sends what was
/// recorded, or what the system re-derives from current CRM state at send time; it never sends
/// what the caller typed. Three reasons, and the first alone is decisive:
/// </para>
///
/// <list type="bullet">
/// <item><b>It would be an unauditable financial write.</b> <c>DebitAccount</c> and
///   <c>ReverseDebit</c> move money on a customer's account in the IMF's own core banking system
///   (ASS-05). With an arbitrary payload accepted here, the single permission
///   <c>Integration.Command.Replay</c> — the tenant Administrator's, per INT-11 — would mean
///   "debit any account for any amount, as a legitimate queued command". And INT-08 requires that
///   no payload VALUE ever reach an audit row (names only, values as <c>"***"</c>), so the one
///   control that would catch the abuse is blinded by the other requirement. The audit would show
///   a named author replaying a command and could not show what they changed.</item>
/// <item><b>It would break INT-34 by construction.</b> A payload is a projection of CRM state at
///   a point in time; an edited one produces a write corresponding to nothing in the CRM, and the
///   daily reconciliation would report the divergence for ever, indistinguishable from a real
///   finding.</item>
/// <item><b>Every failure it would have fixed has a better remedy in the specification.</b> A
///   missing mapping: add the mapping (INT-04) and replay unchanged. Refused credentials: fix the
///   secret (INT-03) and replay unchanged. A wrong VALUE: fix the CRM record, because the CRM is
///   the source of truth — the dispatcher then re-derives the payload from it.</item>
/// </list>
///
/// <para>
/// If payload correction is ever genuinely wanted it is a story of its own, with its own
/// permission, four eyes (M01's client merges and M02's KYC approvals are the two patterns), a
/// per-command-type whitelist of correctable fields, and a field-name diff in the audit. Not a
/// parameter on this record. <c>ReplayCarriesNoCallerPayloadTests</c> fails if one reappears.
/// </para>
/// </summary>
internal sealed record ReplayIntegrationCommandCommand(Guid CommandId)
    : IRequest<Result<ReplayIntegrationCommandResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "IntegrationCommand";

    public string? ResourceId => CommandId.ToString();
}

/// <param name="PayloadFieldNames">
/// The field names the command carries — in clear, because a name is not a value. Empty when the
/// command carries no payload. Names and nothing else, here as in the audit row.
/// </param>
internal sealed record ReplayIntegrationCommandResult(
    Guid CommandId,
    string Status,
    int Attempts,
    IReadOnlyList<string> PayloadFieldNames);
