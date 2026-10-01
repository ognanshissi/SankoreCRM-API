namespace Sankore.Modules.Kyc.Features.Approval.Decide;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// KYC-B-05 — records one decision on the circuit and lets the aggregate route the file.
///
/// <para>
/// Three rules live here and nowhere else:
/// </para>
///
/// <list type="bullet">
/// <item><b>Order.</b> Only the next <c>Pending</c> rung may be signed. A branch manager cannot
///   approve ahead of the agent, and a screen left open on a stale circuit is told
///   <c>KYC_APPROVAL_OUT_OF_ORDER</c> rather than signing a rung it was not showing.</item>
/// <item><b>Four eyes.</b> NOT re-implemented here: <see cref="KycFile.Approve"/> and
///   <see cref="KycFile.Reject"/> own it, this handler calls them and surfaces their error. A
///   second copy of the rule is a second place for it to drift.</item>
/// <item><b>Finality.</b> Only the LAST rung validates the file. Everything below it is a
///   signature; the file stays in <c>Validating</c> and nothing is announced.</item>
/// </list>
///
/// <para>
/// A refusal or a complement request, on the other hand, closes the circuit from ANY rung: the
/// first person who says no is the answer, and making the file climb the remaining rungs to be
/// refused again would only delay what the customer is told.
/// </para>
/// </summary>
internal sealed class DecideKycApprovalHandler(
    KycDbContext db,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<DecideKycApprovalHandler> logger)
    : IRequestHandler<DecideKycApprovalCommand, Result<DecideKycApprovalResult>>
{
    /// <summary>
    /// What <c>KycRejectedEvent.Reason</c> carries when the approver left no comment. A stable
    /// code rather than an empty string: the consumer displays it, and "" reads as a bug.
    /// </summary>
    private const string DefaultRejectionReason = "KYC_APPROVAL_REJECTED";

    public async Task<Result<DecideKycApprovalResult>> Handle(
        DecideKycApprovalCommand cmd, CancellationToken ct)
    {
        if (cmd.Decision == KycApprovalDecision.Pending)
            return Result.Fail<DecideKycApprovalResult>(KycErrors.InvalidTransition);

        // The global query filter scopes this to the caller's tenant: a file of another tenant is
        // simply absent, and the endpoint reports NOT_FOUND rather than FORBIDDEN.
        var file = await db.KycFiles
            .AsTracking()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId, ct);

        if (file is null)
            return Result.Fail<DecideKycApprovalResult>(KycErrors.FileNotFound);

        var steps = await db.KycApprovalSteps
            .AsTracking()
            .Where(s => s.KycFileId == cmd.KycFileId)
            .ToListAsync(ct);

        // Sorted in memory: the level is persisted as text, so an ORDER BY would sort it
        // alphabetically — which agrees with the ladder today and would stop agreeing the first
        // time a level is renamed.
        steps.Sort((a, b) => a.Level.CompareTo(b.Level));

        var orderGuard = GuardOrder(steps, cmd.Level);
        if (orderGuard.IsFailure)
            return Result.Fail<DecideKycApprovalResult>(orderGuard.Error!);

        var step = steps.First(s => s.Level == cmd.Level);
        var isFinalLevel = steps[^1].Level == cmd.Level;

        // Whether THIS approver is entitled to sign THIS level — including a branch manager's
        // temporary delegation while she is away — is not decided here. It is the authorization
        // pipeline's answer: the endpoint requires `kyc:approve`, and M12 grants it for a period
        // and a perimeter through PermissionAttribution. Querying that table from this handler
        // would be a second, divergent implementation of a rule the platform already enforces,
        // and it is why KycErrors.ApprovalStepNotYours is not raised anywhere in this slice.

        // Four eyes, on EVERY rung — not only where the aggregate happens to check.
        //
        // KycFile.Approve and Reject refuse LastSubmittedBy, which covers a rejection and the
        // final approval. An intermediate approval touches no aggregate method, so without this
        // guard the agent who submitted a file could sign level 1 of it themselves and the
        // circuit would show their signature. KYC-B-05 says "jamais valider un dossier qu'il a
        // créé" — it does not say "except on the rungs that are only a signature".
        if (file.LastSubmittedBy == currentUser.Id)
            return Result.Fail<DecideKycApprovalResult>(KycErrors.SelfApprovalForbidden);

        // Read BEFORE the aggregate is touched: Approve sets the tier, and KycTierChangedEvent is
        // about the move from one to the other.
        var previousTier = file.Tier;

        var applied = cmd.Decision switch
        {
            KycApprovalDecision.Rejected => file.Reject(currentUser.Id, clock),

            KycApprovalDecision.ComplementRequired => file.RequestComplement(clock),

            KycApprovalDecision.Approved when isFinalLevel
                => file.Approve(await ResolveTierAsync(file, ct), currentUser.Id, clock),

            // An intermediate approval changes nothing on the file: it is a signature, and the
            // step below is where it is recorded.
            _ => Result.Ok(),
        };

        // The file is moved BEFORE the step is marked, never after. A step is evidence and cannot
        // be un-decided, so recording the signature of a transition that the aggregate then
        // refuses — a self-approval, a file somebody else already rejected — would leave the
        // circuit claiming a decision the file never took.
        if (applied.IsFailure)
            return Result.Fail<DecideKycApprovalResult>(applied.Error!);

        var decided = step.Decide(cmd.Decision, currentUser.Id, clock, cmd.Comment);
        if (decided.IsFailure)
            return Result.Fail<DecideKycApprovalResult>(decided.Error!);

        await PublishOutcomeAsync(cmd, file, previousTier, isFinalLevel, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC file {KycFileId} — {Level} decided {Decision} by {UserId}; file is now {Status}",
            file.Id, cmd.Level, cmd.Decision, currentUser.Id, file.Status);

        var completed = cmd.Decision != KycApprovalDecision.Approved || isFinalLevel;

        return Result.Ok(new DecideKycApprovalResult(
            file.Id, cmd.Level.ToString(), cmd.Decision.ToString(),
            file.Status.ToString(), file.Tier.ToString(), completed));
    }

    /// <summary>
    /// Only the next pending rung may be signed. A file with no circuit at all lands here too, and
    /// gets the same code: there is no rung to sign, which is exactly what out-of-order means from
    /// the caller's point of view.
    /// </summary>
    private static Result GuardOrder(List<KycApprovalStep> steps, KycApprovalLevel level)
    {
        var next = steps.FirstOrDefault(s => s.Decision == KycApprovalDecision.Pending);

        return next is not null && next.Level == level
            ? Result.Ok()
            : Result.Fail(KycErrors.ApprovalOutOfOrder);
    }

    /// <summary>
    /// WHICH TIER — the compliance rule, in one place so it can be found.
    ///
    /// <para>
    /// A file reaches the FULL tier only when both halves of the identity check actually happened:
    /// an identity document that was machine-read (a row with an OCR reading), and a face
    /// comparison that MATCHED. Anything else — a document captured but never read, a face that
    /// was never compared, an approval given on declared data alone — is the SIMPLIFIED tier, and
    /// the ceilings of <c>KycSettingKeys.SimplifiedMaxBalance</c> apply downstream.
    /// </para>
    ///
    /// <para>
    /// Deliberately not derived from the confidence score: a high score on a file with no face
    /// match is a statement about the document, and the full tier is the one that lets a customer
    /// move money without a cap. The evidence rows are what a regulator asks for, so they are what
    /// the tier is read from.
    /// </para>
    /// </summary>
    private async Task<KycTier> ResolveTierAsync(KycFile file, CancellationToken ct)
    {
        var hasDocumentReading = await db.KycIdentityDocuments
            .AnyAsync(d => d.KycFileId == file.Id && d.OcrFieldsJson != null, ct);

        var hasMatchedFace = await db.KycFaceVerifications
            .AnyAsync(v => v.KycFileId == file.Id && v.IsMatch, ct);

        return hasDocumentReading && hasMatchedFace ? KycTier.Full : KycTier.Simplified;
    }

    /// <summary>
    /// Through the outbox of THIS DbContext so the events commit with the decision: an event
    /// without its step would activate a client nobody signed for, and a step without its event
    /// would leave M01 waiting on a file that is already validated.
    ///
    /// <para>
    /// A complement request publishes NOTHING on purpose. The file goes back to the agent inside
    /// M02 and the customer's record elsewhere has not changed — announcing it would be telling
    /// every module about our own paperwork.
    /// </para>
    /// </summary>
    private async Task PublishOutcomeAsync(
        DecideKycApprovalCommand cmd, KycFile file, KycTier previousTier,
        bool isFinalLevel, CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        if (cmd.Decision == KycApprovalDecision.Rejected)
        {
            await publisher.PublishAsync(
                new KycRejectedEvent(
                    TenantId: file.TenantId,
                    CustomerEntityId: file.CustomerId,
                    // The approver's own words. The command's documentation and the endpoint both
                    // say it is a motive shown to operators, never a field value.
                    Reason: string.IsNullOrWhiteSpace(cmd.Comment) ? DefaultRejectionReason : cmd.Comment,
                    RejectedAt: now),
                ct);

            return;
        }

        if (cmd.Decision != KycApprovalDecision.Approved || !isFinalLevel)
            return;

        // The contract M01 already consumes to activate the client. No new event for that: a
        // second one would mean two modules deciding what "validated" means.
        await publisher.PublishAsync(
            new KycValidatedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                ValidatedAt: file.ValidatedAt ?? now),
            ct);

        // And the tier, separately: M03 and M07 cache the ceilings and need the transition itself,
        // not the fact that a file was validated. PreviousTier is read before Approve ran — it is
        // `None` on a first validation, which is what a consumer uses to tell an initial grant
        // from a change.
        await publisher.PublishAsync(
            new KycTierChangedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                PreviousTier: previousTier.ToString(),
                CurrentTier: file.Tier.ToString(),
                ChangedAt: now),
            ct);
    }
}
