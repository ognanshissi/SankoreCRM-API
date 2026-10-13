namespace Sankore.Modules.Kyc.Features.RemoteInvitations.RevokeInvitation;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Features.Files;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class RevokeKycRemoteInvitationHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    KycFileAccess fileAccess,
    TimeProvider clock,
    ILogger<RevokeKycRemoteInvitationHandler> logger)
    : IRequestHandler<RevokeKycRemoteInvitationCommand, Result>
{
    public async Task<Result> Handle(RevokeKycRemoteInvitationCommand cmd, CancellationToken ct)
    {
        // Existence and perimeter in one answer — NOT_FOUND and never FORBIDDEN, as everywhere
        // else: the existence of another branch's file must not leak.
        if (!await fileAccess.CanActOnAsync(cmd.KycFileId, ct))
            return Result.Fail(KycErrors.FileNotFound);

        // Scoped to the file as well as to the id. The route carries both, and matching only the
        // id would let a caller who may see one file revoke a link belonging to another — the
        // perimeter check above would have passed on the wrong file entirely.
        var invitation = await db.KycRemoteInvitations
            .AsTracking()
            .FirstOrDefaultAsync(
                i => i.Id == cmd.InvitationId && i.KycFileId == cmd.KycFileId, ct);

        if (invitation is null)
            return Result.Fail(KycErrors.RemoteInvitationNotFound);

        var revoked = invitation.Revoke(cmd.Reason, currentUser.Id, clock);
        if (revoked.IsFailure)
            return revoked;

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Collection link {InvitationId} of KYC file {KycFileId} revoked by {UserId}",
            cmd.InvitationId, cmd.KycFileId, currentUser.Id);

        return Result.Ok();
    }
}
