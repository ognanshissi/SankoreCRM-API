namespace Sankore.Modules.Kyc.Features.RemoteInvitations.RevokeInvitation;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Withdraws a collection link before it is used (KRM-B-04).
/// </summary>
/// <param name="Reason">
/// Mandatory. A customer's way into their own file disappearing with no recorded motive is
/// something a support call cannot close, and this is a bearer credential being destroyed — the
/// audit trail has to say who and why.
/// </param>
internal sealed record RevokeKycRemoteInvitationCommand(
    Guid KycFileId,
    Guid InvitationId,
    string Reason) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "KycRemoteInvitation";
    public string? ResourceId => InvitationId.ToString();
}
