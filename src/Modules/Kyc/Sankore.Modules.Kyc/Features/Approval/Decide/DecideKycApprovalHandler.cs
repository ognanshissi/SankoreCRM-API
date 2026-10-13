namespace Sankore.Modules.Kyc.Features.Approval.Decide;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Features.RemoteInvitations.IssueInvitation;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Modules.Workflow.PublicApi;
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
    ISender sender,
    IAdministrationModule administration,
    ICurrentUser currentUser,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    IWorkflowModule workflow,
    RemoteSaleEvidence remoteSaleEvidence,
    PepAuthorizationEvidence pepEvidence,
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
        //
        // ManuallyValidatedBy is the second anchor, and it needs the same treatment for the same
        // reason: on a file validated by hand the ladder is the only control left, and an
        // intermediate rung reaches no aggregate method — so without it the validator could sign
        // level 1 of a file they themselves put into the circuit.
        if (currentUser.Id == file.LastSubmittedBy || currentUser.Id == file.ManuallyValidatedBy)
            return Result.Fail<DecideKycApprovalResult>(KycErrors.SelfApprovalForbidden);

        // KRM-B-10: the documents a complement asks for become the scope of a collection link, so
        // they are checked BEFORE the decision is applied rather than when the link is issued. The
        // link is issued best-effort after the commit — a refusal there would be a log line the
        // agent never sees, and they would be left believing they had asked for a utility bill the
        // customer will never be prompted for.
        var scope = await GuardComplementScopeAsync(cmd, file, ct);
        if (scope.IsFailure)
            return Result.Fail<DecideKycApprovalResult>(scope.Error!);

        // CIMA LBC/FT article 13.6, at the last moment it can still be asked for: a file opened
        // remotely needs an accepted, recent proof of address before its holder becomes a
        // customer. Checked only on the FINAL approval — an intermediate signature is a person
        // saying "as far as I am concerned, yes", and blocking it would stop the circuit from
        // progressing while the document is being chased.
        if (cmd.Decision == KycApprovalDecision.Approved && isFinalLevel)
        {
            var evidence = await remoteSaleEvidence.EnsureSatisfiedAsync(file, ct);
            if (evidence.IsFailure)
                return Result.Fail<DecideKycApprovalResult>(evidence.Error!);

            // And, for a politically exposed file, that somebody established where the money
            // comes from. The GeneralManagement rung is the authorisation; this is what that
            // authorisation has to be able to rest on.
            var pep = await pepEvidence.EnsureSatisfiedAsync(file, ct);
            if (pep.IsFailure)
                return Result.Fail<DecideKycApprovalResult>(pep.Error!);
        }

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

        await MirrorToWorkflowAsync(cmd, file, ct);

        await IssueComplementLinkAsync(cmd, file, ct);

        logger.LogInformation(
            "KYC file {KycFileId} — {Level} decided {Decision} by {UserId}; file is now {Status}",
            file.Id, cmd.Level, cmd.Decision, currentUser.Id, file.Status);

        var completed = cmd.Decision != KycApprovalDecision.Approved || isFinalLevel;

        return Result.Ok(new DecideKycApprovalResult(
            file.Id, cmd.Level.ToString(), cmd.Decision.ToString(),
            file.Status.ToString(), file.Tier.ToString(), completed));
    }

    /// <summary>
    /// Refuses a complement that names a document the product's remote policy does not collect.
    ///
    /// <para>
    /// The list offered to the approver comes from the file's own journey (KRM-F-08), so reaching
    /// here with an unknown kind means a screen left open across a product change — or a
    /// hand-made request. Either way the honest answer is "choose again", not a link that asks a
    /// customer for a document the journey will refuse to accept.
    /// </para>
    ///
    /// <para>
    /// The identity document's BACK is admissible even though no product lists it: whether it is
    /// required depends on the document type the customer chose, and <c>KycRemoteJourney</c>
    /// decides that per file rather than per product.
    /// </para>
    /// </summary>
    private async Task<Result> GuardComplementScopeAsync(
        DecideKycApprovalCommand cmd, KycFile file, CancellationToken ct)
    {
        if (cmd.Decision != KycApprovalDecision.ComplementRequired) return Result.Ok();
        if (file.Channel != KycChannel.RemoteSelfService) return Result.Ok();
        if (cmd.RequestedDocuments is not { Count: > 0 } requested) return Result.Ok();

        var policy = file.ProductLineId is { } lineId
            ? await administration.GetKycRemotePolicyAsync(file.TenantId, lineId, ct)
            : null;

        // No policy at all: the link cannot be issued either, and IssueComplementLinkAsync will
        // say so. Refusing the whole decision over it would trap a file whose product an
        // administrator retired — the approver must still be able to send it back.
        if (policy is null) return Result.Ok();

        var collectable = new HashSet<KycDocumentKind>(KycRemoteJourney.AlwaysRequired)
        {
            KycDocumentKind.IdentityDocumentBack,
        };

        foreach (var raw in policy.RequiredDocuments)
        {
            if (Enum.TryParse<KycDocumentKind>(raw, ignoreCase: true, out var kind))
                collectable.Add(kind);
        }

        var unknown = requested.FirstOrDefault(k => !collectable.Contains(k));

        return requested.All(collectable.Contains)
            ? Result.Ok()
            : Result.Fail($"{KycErrors.ComplementDocumentNotCollectable}:{unknown}");
    }

    /// <summary>
    /// KRM-B-10 — a complement asked of a customer who deposited their documents themselves gets
    /// a new collection link, scoped to what the approver listed.
    ///
    /// <para>
    /// <b>Only for a remotely collected file.</b> On a counter file the agent fetches the better
    /// photograph themselves, and mailing their customer a link would be the institution asking
    /// somebody to do work an employee is standing in front of them to do.
    /// </para>
    ///
    /// <para>
    /// <b>Best-effort, after the commit, like the workflow mirror above — and for a stronger
    /// reason.</b> The decision is already recorded and the file is already back with the customer;
    /// the link is how they act on it. Rolling the decision back because a link could not be
    /// issued would discard an approver's judgement over a delivery problem, and the agent can
    /// re-issue by hand from the file's own panel (KRM-F-07), which is exactly the button that
    /// exists for it.
    /// </para>
    ///
    /// <para>
    /// <c>ReplaceActive: true</c>: a customer in this position may still be holding the link they
    /// used, and at most one link into a file is ever live. <c>DelayMinutes: 0</c>: the five-minute
    /// hold exists to let the welcome email arrive first, and that was days ago.
    /// </para>
    /// </summary>
    private async Task IssueComplementLinkAsync(
        DecideKycApprovalCommand cmd, KycFile file, CancellationToken ct)
    {
        if (cmd.Decision != KycApprovalDecision.ComplementRequired) return;
        if (file.Channel != KycChannel.RemoteSelfService) return;

        try
        {
            var issued = await sender.Send(
                new IssueKycRemoteInvitationCommand(
                    TenantId: file.TenantId,
                    KycFileId: file.Id,
                    // The APPROVER, not SYSTEM. A link issued because a named person asked for a
                    // complement is their act, and the invitation's CreatedBy is what tells a
                    // later reader whether the system or a human sent a bearer credential.
                    IssuedBy: currentUser.Id,
                    ReplaceActive: true,
                    DelayMinutes: 0,
                    Purpose: KycRemoteInvitationPurpose.Complement,
                    RequestedKinds: cmd.RequestedDocuments),
                ct);

            if (issued.IsFailure)
            {
                logger.LogWarning(
                    "KYC file {KycFileId}: a complement was requested but no collection link "
                    + "could be issued: {Error}. The agent can re-issue one from the file.",
                    file.Id, issued.Error);

                return;
            }

            logger.LogInformation(
                "KYC file {KycFileId}: complement link {InvitationId} issued for {Count} document(s)",
                file.Id, issued.Value.InvitationId, cmd.RequestedDocuments?.Count ?? 0);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "KYC file {KycFileId}: issuing the complement collection link threw. "
                + "The decision stands and the agent can re-issue one from the file.",
                file.Id);
        }
    }

    /// <summary>
    /// Records this decision on the file's M12 instance, so the cross-module inbox, the audit trail
    /// and the cycle times follow the circuit instead of drifting from it.
    ///
    /// <para>
    /// <b>Best-effort, and that is the point.</b> The decision is already committed when this runs,
    /// and every failure path — no instance, a tenant with no template, an instance M12 timed out,
    /// the module throwing — is a log line. A compliance decision must never be rolled back because
    /// a traceability record could not be written; <c>StartKycApprovalHandler</c> made the same
    /// trade when it opened the instance.
    /// </para>
    ///
    /// <para>
    /// A complement request <b>cancels</b> the instance and clears the link rather than rejecting
    /// it: the file is going back to its author, not being refused, and the next pass gets a fresh
    /// instance so the statistics count attempts instead of showing one interrupted run. The file is
    /// saved again for that, which is why this runs after the main save and not inside it.
    /// </para>
    /// </summary>
    private async Task MirrorToWorkflowAsync(
        DecideKycApprovalCommand cmd, KycFile file, CancellationToken ct)
    {
        if (file.WorkflowInstanceId is not { } instanceId) return;

        var decision = cmd.Decision switch
        {
            KycApprovalDecision.Approved => WorkflowDecision.Approved,
            KycApprovalDecision.Rejected => WorkflowDecision.Rejected,
            KycApprovalDecision.ComplementRequired => WorkflowDecision.Cancelled,
            _ => (WorkflowDecision?)null,
        };

        if (decision is null) return;

        try
        {
            var recorded = await workflow.RecordDecisionAsync(
                new RecordWorkflowDecisionRequest(
                    InstanceId: instanceId,
                    TenantId: file.TenantId,
                    // The rung's position, which is what M12 matches on. KycApprovalLevel's numeric
                    // values ARE the ladder order — the same number KycApprovalStep.LevelRank holds.
                    StepOrder: (int)cmd.Level,
                    Decision: decision.Value,
                    ActedByUserId: currentUser.Id,
                    Comment: cmd.Comment),
                ct);

            if (recorded.IsFailure)
            {
                logger.LogWarning(
                    "KYC file {KycFileId}: the {Decision} on {Level} was not mirrored onto workflow "
                    + "instance {InstanceId}: {Error}. M02 remains the record of the decision.",
                    file.Id, cmd.Decision, cmd.Level, instanceId, recorded.Error);

                return;
            }

            if (decision.Value == WorkflowDecision.Cancelled)
            {
                file.ClearWorkflowInstance();
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "KYC file {KycFileId}: mirroring the {Decision} on {Level} onto workflow instance "
                + "{InstanceId} failed. M02 remains the record of the decision.",
                file.Id, cmd.Decision, cmd.Level, instanceId);
        }
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
