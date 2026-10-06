namespace Sankore.Modules.Kyc.Features.Verification.ManualValidation;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-F-02 — valider les pièces d'un dossier à la main, quand la machine ne peut pas conclure.
///
/// <para>
/// The escape hatch for two states that had none. A biometric service that is down leaves the file
/// in <c>Verifying</c> for the Hangfire replay; a worn document the service keeps refusing bounces
/// the file between <c>ComplementRequired</c> and <c>Verifying</c> indefinitely, because
/// <c>RecordVerification</c> — the only other route into <c>Validating</c> — needs a non-rejected
/// level. Neither case is the customer's fault, and before this there was no way for a human to say
/// so.
/// </para>
///
/// <para>
/// It validates the EVIDENCE, not the file: the approval circuit still decides, and the validator is
/// barred from signing any of its rungs. That is why this is not an approval shortcut — it moves a
/// file to the starting line of the ladder, one rung wider than a scored file's.
/// </para>
///
/// <para>
/// Nothing is published. <c>KycVerificationCompletedEvent</c> describes what the service answered,
/// and here it answered nothing; the events that matter to other modules —
/// <c>KycValidatedEvent</c>, <c>KycTierChangedEvent</c> — are published when the ladder completes,
/// exactly as on the scored path.
/// </para>
/// </summary>
internal sealed class ManuallyValidateKycFileHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    ISender sender,
    TimeProvider clock,
    ILogger<ManuallyValidateKycFileHandler> logger)
    : IRequestHandler<ManuallyValidateKycFileCommand, Result<ManuallyValidateKycFileResult>>
{
    public async Task<Result<ManuallyValidateKycFileResult>> Handle(
        ManuallyValidateKycFileCommand cmd, CancellationToken ct)
    {
        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        if (file is null)
            return Result.Fail<ManuallyValidateKycFileResult>(KycErrors.FileNotFound);

        // The aggregate owns which statuses may move: Verifying (service unreachable) and
        // ComplementRequired (capture refused again and again). Anything else comes back as
        // KYC_INVALID_TRANSITION from the transition table, not from a comparison here.
        var validated = file.ManuallyValidate(currentUser.Id, cmd.Reason, clock);
        if (validated.IsFailure)
            return Result.Fail<ManuallyValidateKycFileResult>(validated.Error!);

        try
        {
            // SAVED BEFORE the circuit is started, and that order is load-bearing:
            // StartKycApprovalHandler re-reads the file through this context, which is NoTracking
            // by default, so it would not observe ManuallyValidatedBy on an uncommitted instance —
            // and KycApprovalCircuit reads exactly that field to add the branch manager. Starting
            // first would silently build the narrow, agent-only ladder the four-eyes anchor then
            // makes unsignable. RunKycVerificationHandler saves before sending for the same reason.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Fail<ManuallyValidateKycFileResult>(KycErrors.ConcurrencyConflict);
        }

        logger.LogWarning(
            "KYC file {KycFileId} was validated MANUALLY by {UserId} with no biometric score; "
            + "it enters the approval circuit and {UserId} cannot sign it",
            file.Id, currentUser.Id, currentUser.Id);

        var started = await sender.Send(
            new StartKycApprovalCommand(file.TenantId, file.Id, currentUser.Id), ct);

        if (started.IsFailure)
        {
            // The file is in Validating either way. Refusing here would roll back a decision an
            // operator took, over a circuit the screen can still create — the same trade
            // RunKycVerificationHandler makes.
            logger.LogError(
                "KYC file {KycFileId} was manually validated but its approval circuit could not be "
                + "started: {Error}", file.Id, started.Error);

            return Result.Ok(new ManuallyValidateKycFileResult(
                file.Id, file.Status.ToString(), []));
        }

        return Result.Ok(new ManuallyValidateKycFileResult(
            file.Id, file.Status.ToString(), started.Value.Levels));
    }
}
