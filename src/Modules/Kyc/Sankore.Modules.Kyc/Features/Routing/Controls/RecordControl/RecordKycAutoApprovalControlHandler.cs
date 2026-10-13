namespace Sankore.Modules.Kyc.Features.Routing.Controls.RecordControl;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Reviews.RaiseReview;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// Records the control and, when it says the machine was wrong, opens the review that is the only
/// remaining remedy (KRM-B-11).
///
/// <para>
/// <b>Four eyes, and the one it has to enforce.</b> The officer may not control an approval they
/// themselves could have influenced — except there is nobody to compare against: an automatic
/// approval was signed by SYSTEM. So the guard here is the opposite one, and it is about the file
/// rather than the approval: a compliance officer who also happens to be the file's responsible
/// agent is refused, because the person whose conversion produced the file must not be the person
/// who certifies that a machine was right to validate it unattended. <c>LastSubmittedBy</c> is
/// SYSTEM on these files and is therefore useless as an anchor — <c>ResponsibleAgentId</c> is the
/// only human the file names.
/// </para>
///
/// <para>
/// <b>The review is raised AFTER the verdict is committed, and its failure does not undo it.</b> A
/// control that is recorded without its review is a true statement about what happened and leaves a
/// visible row an officer can act on; a verdict rolled back because a review could not be scheduled
/// would lose the finding itself, which is the one thing that cannot be reconstructed.
/// </para>
/// </summary>
internal sealed class RecordKycAutoApprovalControlHandler(
    KycDbContext db,
    ISender sender,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<RecordKycAutoApprovalControlHandler> logger)
    : IRequestHandler<RecordKycAutoApprovalControlCommand, Result<RecordKycAutoApprovalControlResult>>
{
    /// <summary>
    /// What the raised review says. A fixed sentence rather than the officer's finding: the review
    /// reason is read by whoever picks it up and is stored in clear, while the finding is free text
    /// an officer typed — and the two have different audiences. The finding stays on the control row,
    /// where the officer's own screen shows it next to this review.
    /// </summary>
    internal const string ReviewReason =
        "Contrôle a posteriori : approbation automatique jugée non conforme";

    public async Task<Result<RecordKycAutoApprovalControlResult>> Handle(
        RecordKycAutoApprovalControlCommand cmd, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant: a control of another tenant
        // is simply absent, and the endpoint reports NOT_FOUND rather than FORBIDDEN.
        var control = await db.KycAutoApprovalControls
            .AsTracking()
            .FirstOrDefaultAsync(c => c.Id == cmd.ControlId, ct);

        if (control is null)
        {
            return Result.Fail<RecordKycAutoApprovalControlResult>(
                KycErrors.AutoApprovalControlNotFound);
        }

        var responsibleAgentId = await db.KycFiles
            .Where(f => f.Id == control.KycFileId)
            .Select(f => f.ResponsibleAgentId)
            .FirstOrDefaultAsync(ct);

        // See the class remarks. Not SelfApprovalForbidden by another name: the act being refused
        // is certifying an unattended validation of a file this person brought in.
        if (responsibleAgentId is { } agent && agent == currentUser.Id)
        {
            return Result.Fail<RecordKycAutoApprovalControlResult>(
                KycErrors.SelfApprovalForbidden);
        }

        var recorded = control.Record(cmd.Compliant, currentUser.Id, cmd.Finding, clock);
        if (recorded.IsFailure)
            return Result.Fail<RecordKycAutoApprovalControlResult>(recorded.Error!);

        await db.SaveChangesAsync(ct);

        var reviewId = cmd.Compliant ? null : await RaiseReviewAsync(control, ct);

        logger.LogInformation(
            "Automatic approval of KYC file {KycFileId} controlled by {UserId}: {Status}",
            control.KycFileId, currentUser.Id, control.Status);

        return Result.Ok(new RecordKycAutoApprovalControlResult(
            control.Id, control.KycFileId, control.Status.ToString(), reviewId));
    }

    /// <summary>
    /// Opens the event-driven review KYC-B-07 already knows how to run, and links it to the
    /// control so nobody raises a second one by hand.
    ///
    /// <para>
    /// Best-effort: see the class remarks. A failure is a log line and a null in the response,
    /// which the screen turns into "raise a review on this file" rather than hiding.
    /// </para>
    /// </summary>
    private async Task<Guid?> RaiseReviewAsync(
        KycAutoApprovalControl control, CancellationToken ct)
    {
        try
        {
            var raised = await sender.Send(
                new RaiseKycReviewCommand(control.KycFileId, ReviewReason, currentUser.Id), ct);

            if (raised.IsFailure)
            {
                logger.LogWarning(
                    "KYC file {KycFileId} was found non-compliant but no review could be raised: "
                    + "{Error}. The finding stands and a review can be raised from the file.",
                    control.KycFileId, raised.Error);

                return null;
            }

            control.LinkReview(raised.Value.ReviewId);
            await db.SaveChangesAsync(ct);

            return raised.Value.ReviewId;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Raising the review for the non-compliant control of KYC file {KycFileId} threw. "
                + "The finding stands.",
                control.KycFileId);

            return null;
        }
    }
}
