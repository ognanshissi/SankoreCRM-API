namespace Sankore.Modules.Kyc.Features.PublicRemote;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Verification.RunVerification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// The customer presses send (KRM-B-07): the link is spent, the file becomes a remote
/// self-collection, and the verification runs with no employee behind it.
///
/// <para>
/// <b>Why <c>LastSubmittedBy</c> is SYSTEM and not the agent who converted the lead.</b> That field
/// is the four-eyes anchor: <c>KycFile.Approve</c> refuses whoever it names. If the converting agent
/// were recorded here they would be barred from deciding on a file whose evidence they never
/// touched — and on the manual route they are exactly the person KRM-B-09 sends it to. A customer's
/// own upload was produced by nobody inside the institution, so nobody inside the institution is
/// disqualified by it. That is the whole reason the remote path can route to one human instead of
/// two.
/// </para>
///
/// <para>
/// <b>Ordering.</b> The file and the link are committed first, then the verification is dispatched.
/// It is not one transaction and cannot be: the verification calls an external service and can take
/// seconds. Committing first means a crash in between leaves a submitted file in <c>Verifying</c>
/// with no verification — which the Hangfire replay and the compliance backlog both surface.
/// Dispatching first would mean a verification against a link the customer could still re-submit.
/// </para>
/// </summary>
internal sealed class KycRemoteSubmission(
    KycDbContext db,
    ISender sender,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<KycRemoteSubmission> logger)
{
    internal async Task<Result> SubmitAsync(
        KycRemoteInvitation invitation,
        KycRemoteJourney.JourneyState state,
        CancellationToken ct)
    {
        var file = await db.KycFiles
            .IgnoreQueryFilters()
            .AsTracking()
            .FirstOrDefaultAsync(
                f => f.TenantId == invitation.TenantId && f.Id == invitation.KycFileId, ct);

        if (file is null)
            return Result.Fail(KycErrors.FileNotFound);

        // The two images the verification needs, as the customer last sent them. Resolved here
        // rather than inside the command because the command takes opaque references — it never
        // learns which kind they were, and must not.
        var front = await CurrentRefAsync(invitation, KycDocumentKind.IdentityDocumentFront, ct);
        var selfie = await CurrentRefAsync(invitation, KycDocumentKind.Selfie, ct);

        if (front is null || selfie is null)
        {
            // The journey already refused on missing documents, so this is a store that lost an
            // object between two requests. Reported rather than swallowed: the customer must not be
            // told their file went in when it did not.
            logger.LogError(
                "Remote submission of KYC file {KycFileId} has no stored {Missing}",
                file.Id, front is null ? "identity document" : "selfie");
            return Result.Fail(KycErrors.VerificationImageNotFound);
        }

        var declaredType = state.ChosenType ?? KycDocumentType.Cni;

        // SYSTEM — see the class remarks. Guid.Empty is this codebase's SYSTEM actor.
        var submitted = file.SubmitForVerification(Guid.Empty, clock);
        if (submitted.IsFailure)
            return submitted;

        // The channel moves HERE and not when the link was sent: a link nobody used is not a
        // remote enrolment. It is what the scorer is weighted on, what CIMA 13.6's proof-of-address
        // gate keys on, and what tells a validator no employee ever saw this document.
        file.MarkRemotelyCollected(clock);

        var spent = invitation.MarkSubmitted(clock);
        if (spent.IsFailure)
            return spent;

        await publisher.PublishAsync(
            new KycRemoteSubmittedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                KycFileId: file.Id,
                InvitationId: invitation.Id,
                ProductLineId: file.ProductLineId,
                ResponsibleAgentId: file.ResponsibleAgentId,
                SubmittedAt: invitation.SubmittedAt ?? clock.GetUtcNow()),
            ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC file {KycFileId} submitted remotely by its customer (link {InvitationId})",
            file.Id, invitation.Id);

        // AFTER the commit. A verification that ran inside this unit of work would reach the
        // external service before the submission was durable, and its own writes would then be
        // rolled back with ours if anything above failed.
        //
        // Not awaited for its outcome: whatever the service answers, the customer's answer is the
        // same — "your file was sent". The command records the outcome on the file, queues its own
        // replay when the service is unreachable, and the routing of KRM-B-08 picks it up from
        // there.
        var verification = await sender.Send(new RunKycVerificationCommand(
            TenantId: file.TenantId,
            KycFileId: file.Id,
            DocumentStorageRef: front,
            SelfieStorageRef: selfie,
            // SYSTEM again, and it must match what was written above: RunKycVerificationHandler
            // sets LastSubmittedBy from this when the file is still collecting, and a different
            // actor here would quietly re-anchor the four-eyes rule on somebody.
            RequestedBy: Guid.Empty,
            DocumentType: declaredType), ct);

        if (verification.IsFailure)
        {
            // The submission STANDS. The file is submitted, the link is spent, and the customer has
            // been told so — a verification that could not start is the institution's problem to
            // resolve, not a reason to hand the customer an error about a file they have already
            // sent.
            logger.LogError(
                "Verification of remotely submitted KYC file {KycFileId} could not start: {Error}",
                file.Id, verification.Error);
        }

        return Result.Ok();
    }

    /// <summary>
    /// The storage reference of the customer's most recent photograph of one kind.
    /// </summary>
    private async Task<string?> CurrentRefAsync(
        KycRemoteInvitation invitation, KycDocumentKind kind, CancellationToken ct)
        => await db.KycDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == invitation.TenantId
                     && d.KycFileId == invitation.KycFileId
                     && d.Kind == kind)
            .OrderByDescending(d => d.UploadedAt)
            .Select(d => d.StorageRef)
            .FirstOrDefaultAsync(ct);
}
