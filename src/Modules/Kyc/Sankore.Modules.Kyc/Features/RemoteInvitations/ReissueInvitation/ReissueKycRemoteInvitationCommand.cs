namespace Sankore.Modules.Kyc.Features.RemoteInvitations.ReissueInvitation;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// An AGENT asks for a customer's collection link to be re-sent (KRM-B-04).
///
/// <para>
/// <b>Why this is a second command and not a flag on
/// <see cref="IssueKycRemoteInvitationCommand"/>.</b> That command has two callers with genuinely
/// different authority: a conversion, running as SYSTEM with no perimeter of its own, and an agent
/// acting on a file they must be entitled to see. The first version conflated them, and the
/// consequence was an IDOR — the endpoint carried <c>kyc:remote-invite</c> and nothing else, so an
/// agent in one branch who held a file id from another could have the system mail a single-use
/// link into that branch's customer file, and revoke the link that customer was already holding.
/// </para>
///
/// <para>
/// A boolean on the shared command would have closed the hole and left the trap: the unscoped path
/// would still be one parameter away from any future endpoint. Splitting it means the HTTP surface
/// can only reach the scoped command, and the unscoped one stays <c>internal</c> and reachable
/// from <c>KycRequestedConsumer</c> alone.
/// </para>
/// </summary>
internal sealed record ReissueKycRemoteInvitationCommand(
    Guid KycFileId,
    /// <summary>
    /// The channel the agent picked. E-mail by default, so a caller written before the choice
    /// existed keeps the behaviour it was written against.
    /// </summary>
    KycRemoteInvitationChannel Channel = KycRemoteInvitationChannel.Email)
    : IRequest<Result<IssueKycRemoteInvitationResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "KycRemoteInvitation";

    /// <summary>
    /// The FILE: the invitation's id does not exist when the audit entry is written, and "an agent
    /// re-sent the link of this file" is the sentence an auditor is looking for.
    /// </summary>
    public string? ResourceId => KycFileId.ToString();
}
