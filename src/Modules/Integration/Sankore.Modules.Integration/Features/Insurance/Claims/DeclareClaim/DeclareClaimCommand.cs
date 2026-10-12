namespace Sankore.Modules.Integration.Features.Insurance.Claims.DeclareClaim;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-09, first criterion: an agent declares a loss on a policy, and the declaration reaches the
/// insurer as a queued command.
///
/// <para>
/// <b>The dossier row and the command are written in ONE transaction</b>, the same rule ASS-04's
/// subscription follows: a declaration queued with no dossier is a loss nobody can follow up, and
/// a dossier with no queued command is a claim the insurer never hears about. The second is the
/// worse of the two — the customer has been told their claim is in — which is why neither is
/// allowed to exist alone.
/// </para>
///
/// <para>
/// <b>No documents travel with this command yet.</b> <c>InsuranceClaimPayload</c> carries
/// <c>DocumentStorageRefs</c> and it is deliberately sent EMPTY here: a <i>pièce de sinistre</i>
/// may not leave the platform before the antivirus pass has run on it
/// (<c>ClaimDocument.IsTransmittable</c>), and nothing has been scanned at the moment a
/// declaration is made. The upload, the scan and the transmission are their own wave; the insurer
/// asking for documents afterwards is the ordinary path and is what
/// <c>ClaimStatus.DocumentsRequired</c> is for.
/// </para>
/// </summary>
/// <param name="Description">
/// The agent's account of the loss, in clear on the way in and ENCRYPTED in the column. Free text
/// about a named person's misfortune — a death, an illness, an accident — so it is
/// <see cref="SensitiveDataAttribute"/>: the audit row records that the field was written, never
/// what was in it.
/// </param>
internal sealed record DeclareClaimCommand(
    Guid PolicyId,
    DateOnly OccurredOn,
    string Nature,
    [property: SensitiveData] string? Description)
    : IRequest<Result<DeclareClaimResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "InsuranceClaim";

    // The POLICY, because the claim's own id does not exist until this command succeeds. An audit
    // row naming nothing is an audit row nobody can search.
    public string ResourceId => PolicyId.ToString();
}

/// <param name="AlreadyExisted">
/// True when this loss was already declared — same policy, same date, same nature. Success and not
/// a conflict: the agent asked for the loss to be declared and the loss is declared. A second
/// dossier for one event is what <c>ux_ins_claim_declaration</c> exists to prevent.
/// </param>
public sealed record DeclareClaimResult(
    Guid ClaimId,
    string Status,
    bool AlreadyExisted,
    Guid? DeclareCommandId);

/// <summary>Request body. Mirrors the command minus what the server decides.</summary>
public sealed record DeclareClaimRequest(
    Guid PolicyId,
    DateOnly OccurredOn,
    string Nature,
    string? Description);
