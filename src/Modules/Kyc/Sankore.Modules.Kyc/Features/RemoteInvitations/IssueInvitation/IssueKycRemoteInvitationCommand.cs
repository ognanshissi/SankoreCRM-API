namespace Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Issues a single-use collection link for a KYC file and queues the email that carries it
/// (KRM-B-03, KRM-B-04).
///
/// <para>
/// One command for both callers — the conversion, through <c>KycRequestedConsumer</c>, and an
/// agent clicking "renvoyer" — because the eligibility rules are the same and a second
/// implementation of them is a second answer to "may this customer be sent a link". What differs
/// is the two parameters below.
/// </para>
/// </summary>
/// <param name="IssuedBy">
/// <c>Guid.Empty</c> for the automatic issue at a conversion, the agent's id otherwise. It is
/// recorded on the invitation and is what distinguishes a link the system sent from one a human
/// asked for — the audit trail of a bearer credential has to answer that.
/// </param>
/// <param name="ReplaceActive">
/// Whether an existing, still-usable link is revoked and replaced.
///
/// <para>
/// <b>False for the automatic path, and that is the idempotency.</b> A redelivered conversion
/// event must not mail a second link, nor invalidate the one the customer is already holding, so
/// it reports the existing invitation and sends nothing. <b>True for an agent</b>, who clicks
/// precisely because the first link did not work: the old one is revoked so that at most one link
/// into a file is ever live.
/// </para>
/// </param>
/// <param name="DelayMinutes">
/// How long to hold the email back. Null means "read the tenant's
/// <c>remote-email-delay-minutes</c>", which is what the conversion wants — the link must land
/// after the welcome message that carries the client number. An agent re-issuing passes 0: the
/// welcome mail went out days ago and they are usually on the phone with the customer.
/// </param>
internal sealed record IssueKycRemoteInvitationCommand(
    Guid TenantId,
    Guid KycFileId,
    Guid IssuedBy,
    bool ReplaceActive,
    int? DelayMinutes = null,
    KycRemoteInvitationPurpose Purpose = KycRemoteInvitationPurpose.Initial
) : IRequest<Result<IssueKycRemoteInvitationResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycRemoteInvitation";

    /// <summary>
    /// The FILE, not the invitation: the invitation's id does not exist when the audit entry is
    /// written, and "a link was issued for this file" is the sentence an auditor is looking for.
    /// </summary>
    public string? ResourceId => KycFileId.ToString();
}

/// <param name="Reused">
/// True when a usable link already existed and nothing was sent. The command SUCCEEDS in that case
/// — the caller asked for the customer to hold a link and the customer holds one — but a consumer
/// uses this to stay quiet instead of logging a second issue.
/// </param>
internal sealed record IssueKycRemoteInvitationResult(
    Guid InvitationId,
    DateTimeOffset ExpiresAt,
    bool Reused);
