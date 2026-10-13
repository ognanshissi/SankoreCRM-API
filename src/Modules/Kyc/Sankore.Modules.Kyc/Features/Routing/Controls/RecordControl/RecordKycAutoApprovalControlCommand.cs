namespace Sankore.Modules.Kyc.Features.Routing.Controls.RecordControl;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An officer's verdict on one sampled automatic approval (KRM-B-11).
///
/// <para>
/// The officer is NOT a field: it is <c>ICurrentUser</c>, read in the handler. A caller-supplied
/// reviewer id would let somebody attribute a control to a colleague, and the whole value of this
/// table is that it names who actually looked.
/// </para>
/// </summary>
/// <param name="Compliant">
/// True when the machine was right to approve. False opens an event-driven review on the file —
/// the only thing that can still be done about a relationship already entered into.
/// </param>
/// <param name="Finding">
/// What the officer found. Mandatory when <paramref name="Compliant"/> is false: it is what the
/// colleague picking up the review reads. Stored in clear and shown to operators, so it must never
/// carry a document number, a date of birth or an address.
/// </param>
internal sealed record RecordKycAutoApprovalControlCommand(
    Guid ControlId,
    bool Compliant,
    string? Finding
) : IRequest<Result<RecordKycAutoApprovalControlResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycAutoApprovalControl";
    public string? ResourceId => ControlId.ToString();
}

/// <param name="RaisedReviewId">
/// The review opened by a non-compliant verdict, or null — null on a compliant one, and also null
/// when the verdict stands but the review could not be scheduled, which the response says so a
/// screen can tell the officer to raise one by hand.
/// </param>
internal sealed record RecordKycAutoApprovalControlResult(
    Guid ControlId,
    Guid KycFileId,
    string Status,
    Guid? RaisedReviewId);
