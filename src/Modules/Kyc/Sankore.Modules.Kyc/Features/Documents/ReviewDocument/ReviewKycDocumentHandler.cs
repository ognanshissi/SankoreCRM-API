namespace Sankore.Modules.Kyc.Features.Documents.ReviewDocument;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-F-02 — un validateur accepte ou refuse une pièce, avec motif.
///
/// <para>
/// Two rules the product owner fixed explicitly, and which this handler exists to hold:
/// </para>
///
/// <list type="bullet">
/// <item><b>An acceptance moves nothing.</b> Accepting the last outstanding document does NOT
///   advance the file. The validator still takes the file-level decision afterwards — either the
///   approval ladder, or manual validation when the machine could not answer. Nothing changes
///   status as a side effect of a document edit, so the ladder keeps its meaning.</item>
/// <item><b>A refusal sends the file back for a complement</b>, carrying the motive on the document
///   row. <c>ComplementRequired</c> is the status M02 already owns for "we need a better one", and
///   <c>ListKycFilesHandler.RequiredAction</c> already reports <c>PROVIDE_COMPLEMENT</c> for it.</item>
/// </list>
///
/// <para>
/// NO approval step is touched. The existing <c>ComplementRequired</c> decision burns a rung of the
/// circuit — it is a decision an approver took — whereas this validator holds no rung, so forging
/// one would put their name on a signature they never gave. The consequence is deliberate and the
/// screen must live with it: <c>GET approval/circuit</c> shows an untouched ladder, and the reason
/// the file left <c>Validating</c> is in <c>kyc_documents</c> instead.
/// </para>
///
/// <para>
/// Nothing is published. The existing complement path is silent on purpose — the file went back to
/// an agent inside M02 and no other module's view of the customer changed — and a document refusal
/// is the same event seen closer up.
/// </para>
/// </summary>
internal sealed class ReviewKycDocumentHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<ReviewKycDocumentHandler> logger)
    : IRequestHandler<ReviewKycDocumentCommand, Result<ReviewKycDocumentResult>>
{
    /// <summary>
    /// The two statuses where a human verdict on an image can still change the outcome.
    ///
    /// <para>
    /// <c>Validating</c> is the ordinary case: the file is waiting on exactly this. <c>Verifying</c>
    /// is included because it is where a file parks when the biometric service is unreachable —
    /// <c>RunKycVerificationHandler</c> saves it there for the Hangfire replay — and that is
    /// precisely when somebody has to look at the images by hand. Excluding it would leave the
    /// outage case reviewable only after the override, which is backwards.
    /// </para>
    ///
    /// <para>
    /// <c>Collecting</c> is excluded: <c>Collecting → ComplementRequired</c> is not in the
    /// transition table, and a file in collection is already with the agent, which is what
    /// <c>ComplementRequired</c> means. Everything later is decided.
    /// </para>
    /// </summary>
    private static bool CanReviewDocuments(KycFileStatus status) =>
        status is KycFileStatus.Verifying or KycFileStatus.Validating;

    public async Task<Result<ReviewKycDocumentResult>> Handle(
        ReviewKycDocumentCommand cmd, CancellationToken ct)
    {
        // The global query filter scopes this to the caller's tenant: a file of another tenant is
        // simply absent, and the endpoint answers NOT_FOUND rather than FORBIDDEN — the existence
        // of a customer must not leak across tenants.
        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        if (file is null)
            return Result.Fail<ReviewKycDocumentResult>(KycErrors.FileNotFound);

        if (!CanReviewDocuments(file.Status))
            return Result.Fail<ReviewKycDocumentResult>(KycErrors.InvalidTransition);

        // AsTracking: the context is NoTracking by default, so without it Review() would mutate a
        // detached instance and SaveChanges would write nothing at all.
        var document = await db.KycDocuments
            .AsTracking()
            .FirstOrDefaultAsync(d => d.Id == cmd.DocumentId && d.KycFileId == cmd.KycFileId, ct);

        // Scoped to the file in the same query: a document id that exists under another file is
        // reported absent rather than reviewed through the wrong file.
        if (document is null)
            return Result.Fail<ReviewKycDocumentResult>(KycErrors.DocumentNotFound);

        var isCurrent = !await db.KycDocuments
            .AnyAsync(d => d.KycFileId == cmd.KycFileId
                        && d.Kind == document.Kind
                        && d.UploadedAt > document.UploadedAt, ct);

        // A superseded image is not what the file is waiting on. Refusing one would send the whole
        // file back for a complement over a photograph the agent already replaced.
        if (!isCurrent)
            return Result.Fail<ReviewKycDocumentResult>(KycErrors.DocumentNotCurrent);

        // The FILE moves before the decision is written, never after — the ordering invariant
        // DecideKycApprovalHandler spells out. A decision is evidence and cannot be un-taken, so
        // recording one for a transition the aggregate then refuses would leave the document
        // claiming a consequence the file never had.
        if (cmd.Decision == KycDocumentReviewDecision.Refused)
        {
            var moved = file.RequestComplement(clock);
            if (moved.IsFailure)
                return Result.Fail<ReviewKycDocumentResult>(moved.Error!);
        }

        var reviewed = document.Review(cmd.Decision, currentUser.Id, clock, cmd.Reason);
        if (reviewed.IsFailure)
            return Result.Fail<ReviewKycDocumentResult>(reviewed.Error!);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two validators on the same document. The decide-once guard above is an in-memory
            // comparison, so both of them passed it; the xmin token is what stops the second write
            // from overwriting a recorded decision and its author.
            return Result.Fail<ReviewKycDocumentResult>(KycErrors.ConcurrencyConflict);
        }

        // The motive is NOT logged: it is operator-written free text, and logs travel further than
        // the KYC screen. Who decided what, on which document, is enough here — the motive itself
        // is on the row and in the audit entry.
        logger.LogInformation(
            "KYC document {DocumentId} ({Kind}) of file {KycFileId} was {Decision} by {UserId}; "
            + "file is now {Status}",
            document.Id, document.Kind, file.Id, cmd.Decision, currentUser.Id, file.Status);

        return Result.Ok(new ReviewKycDocumentResult(
            document.Id,
            document.Kind.ToString(),
            document.ReviewDecision.ToString(),
            file.Status.ToString()));
    }
}
