namespace Sankore.Modules.Kyc.Features.RemoteInvitations.ReissueInvitation;

using MediatR;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ReissueKycRemoteInvitationHandler(
    KycFileAccess fileAccess,
    ICurrentUser currentUser,
    ISender sender)
    : IRequestHandler<ReissueKycRemoteInvitationCommand, Result<IssueKycRemoteInvitationResult>>
{
    public async Task<Result<IssueKycRemoteInvitationResult>> Handle(
        ReissueKycRemoteInvitationCommand cmd, CancellationToken ct)
    {
        // FIRST, before anything is read about the file and before any link is revoked. The
        // permission on the endpoint says what verbs this agent has; it says nothing about which
        // files they have them on.
        //
        // Reported as NOT_FOUND and never FORBIDDEN: a 403 would confirm that another branch's
        // file exists, which is the one thing the perimeter is meant to hide. Same answer as every
        // other single-file read in this module.
        if (!await fileAccess.CanActOnAsync(cmd.KycFileId, ct))
            return Result.Fail<IssueKycRemoteInvitationResult>(KycErrors.FileNotFound);

        return await sender.Send(
            new IssueKycRemoteInvitationCommand(
                TenantId: currentUser.TenantId,
                KycFileId: cmd.KycFileId,
                // The agent, so the row records that a human asked for this credential to be
                // posted rather than the conversion.
                IssuedBy: currentUser.Id,
                // An agent clicking this wants the first link gone: they are clicking because it
                // did not work, and two live links into one file means an intercepted older one
                // still works.
                ReplaceActive: true,
                // And they want the new one now. The tenant delay exists to order two messages at
                // a conversion; the welcome mail is long gone by the time anybody re-issues.
                DelayMinutes: 0),
            ct);
    }
}
