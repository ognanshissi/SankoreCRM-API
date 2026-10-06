namespace Sankore.Modules.Kyc.Features.Verification;

using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Verification.RunVerification;
using Sankore.Shared.Infrastructure.BackgroundJobs;

/// <summary>
/// Hangfire job — re-runs a verification the biometric service could not answer.
///
/// <para>
/// It exists for exactly one of the three biometric outcomes: <b>Unavailable</b>. A refused
/// capture is never replayed — the service worked, the photo is unusable, and re-sending the same
/// bytes would get the same answer forever. Only an outage is worth trying again, and only because
/// the file was deliberately left in Verifying rather than pushed back to the agent.
/// </para>
///
/// <para>
/// Payload is opaque identifiers only: the file id, the tenant, and the two storage references the
/// document store issued. No document number, no OCR reading, no image — a Hangfire payload is a
/// row in a shared database and a line in a dashboard, and neither is a place for KYC evidence.
/// </para>
/// </summary>
public sealed class ReplayKycVerificationJob(IServiceScopeFactory scopeFactory)
{
    /// <param name="requestedBy">
    /// The ORIGINAL agent, carried through every replay. The verification is still their
    /// submission: replacing them with SYSTEM here would also replace the file's
    /// <c>LastSubmittedBy</c>, and the four-eyes rule would then let them approve their own file.
    /// </param>
    /// <param name="documentType">
    /// Carried through the replay rather than defaulted: a retry that re-declared every document
    /// as a CNI would have the service read a passport with the wrong template, and the file would
    /// come back "unreadable" for a reason no log mentions.
    /// </param>
    /// <param name="attempt">
    /// Which attempt this is. Passed straight to the command, which decides whether a further
    /// replay is queued — the budget lives in one place, next to the decision that spends it.
    /// </param>
    public async Task ExecuteAsync(
        Guid kycFileId,
        Guid tenantId,
        string documentStorageRef,
        string selfieStorageRef,
        Guid requestedBy,
        KycDocumentType documentType,
        int attempt)
    {
        // Set BEFORE the scope is created: ICurrentUser and ITenantContext are resolved from this
        // ambient context when the scope builds them, so a scope opened first would capture the
        // HTTP implementations and find no request. The actor stays the original agent; only the
        // display name says SYSTEM, which is what the audit trail shows for an automatic retry.
        using var bgCtx = BackgroundJobContext.SetScope(tenantId, requestedBy, "SYSTEM");
        using var scope = scopeFactory.CreateScope();

        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<ReplayKycVerificationJob>>();

        try
        {
            var result = await sender.Send(new RunKycVerificationCommand(
                TenantId: tenantId,
                KycFileId: kycFileId,
                DocumentStorageRef: documentStorageRef,
                SelfieStorageRef: selfieStorageRef,
                RequestedBy: requestedBy,
                DocumentType: documentType,
                Attempt: attempt));

            if (result.IsFailure)
            {
                logger.LogWarning(
                    "Replay {Attempt} of the KYC verification of file {KycFileId} was refused: {Error}",
                    attempt, kycFileId, result.Error);
                return;
            }

            logger.LogInformation(
                "Replay {Attempt} of the KYC verification of file {KycFileId} ended as {Outcome}; "
                + "file is now {Status}",
                attempt, kycFileId, result.Value.Outcome, result.Value.Status);
        }
        catch (Exception ex)
        {
            // Swallowed and logged rather than rethrown: Hangfire would retry on its own schedule,
            // in parallel with the backoff the handler already queued, and the two would race on
            // the same file. The handler owns the retry budget — see MaxReplayAttempts.
            logger.LogError(
                ex, "Replay {Attempt} of the KYC verification of file {KycFileId} threw",
                attempt, kycFileId);
        }
    }
}
