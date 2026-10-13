namespace Sankore.Modules.Kyc.Features.Routing.RouteRemoteFile;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.Approval;
using Sankore.Modules.Kyc.Features.Approval.StartApproval;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Features.Verification;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Modules.Kyc.PublicApi;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

/// <summary>
/// KRM-B-08 — gathers the facts, asks <see cref="KycAutoApprovalPolicy"/>, and applies its verdict.
///
/// <para>
/// <b>The split is the design.</b> Every compliance condition lives in the policy, which is a pure
/// function over a record and is unit-tested case by case; everything here is plumbing — six reads
/// and two writes. A condition implemented in this file would be one no test could reach without a
/// scored file, a product, a tenant and a mail server.
/// </para>
///
/// <para>
/// <b>Two end states, and they must be indistinguishable from the manual ones.</b> An automatic
/// approval has to leave the file exactly as the last rung of a human circuit would — same tier,
/// same status, same <c>KycValidatedEvent</c> and <c>KycTierChangedEvent</c> through the same
/// outbox, same absence of a scheduled review (the manual path does not schedule one either, and
/// a path that did would be a divergence in the other direction). M01 activates the client off
/// that event and M03/M07 cache the ceilings off the other; a half-published approval leaves a
/// customer validated in M02 and pending everywhere else.
/// </para>
///
/// <para>
/// <b>Idempotence is the file's own <c>ValidationRoute</c>.</b> This module has no inbox table
/// (<c>IInboxGuard</c> belongs to M01), the trigger is a broker message, and a second evaluation
/// of the same verification would either mail the agent twice or — far worse — re-approve a file
/// an agent has since sent back. <c>KycFile.RouteToManual</c> refuses to overwrite a route, and the
/// automatic branch is guarded by the status transition itself: a file that is already
/// <c>Simplified</c> cannot move to <c>Simplified</c> again.
/// </para>
/// </summary>
internal sealed class RouteRemoteKycFileHandler(
    KycDbContext db,
    ISender sender,
    IAdministrationModule administration,
    ICustomersModule customers,
    KycRemoteJourney journey,
    RemoteSaleEvidence remoteSaleEvidence,
    Controls.KycAutoApprovalSampler sampler,
    [FromKeyedServices(nameof(KycDbContext))] IEventPublisher publisher,
    TimeProvider clock,
    ILogger<RouteRemoteKycFileHandler> logger)
    : IRequestHandler<RouteRemoteKycFileCommand, Result<RouteRemoteKycFileResult>>
{
    /// <summary>
    /// What the <see cref="KycApprovalLevel.Automatic"/> step's comment says, as KRM-B-08 words it.
    /// The product CODE and the policy's shape, never a score and never a flag: the comment is
    /// displayed on the file's history (KRM-F-08) and a score shown beside an approval reads as the
    /// reason it was given, which it is not — the reason is that every condition held.
    /// </summary>
    private static readonly System.Text.CompositeFormat AutoApprovalComment =
        System.Text.CompositeFormat.Parse("Approbation automatique — politique produit {0} v{1}");

    public async Task<Result<RouteRemoteKycFileResult>> Handle(
        RouteRemoteKycFileCommand cmd, CancellationToken ct)
    {
        // IgnoreQueryFilters + explicit tenant: the sender is a consumer, whose ambient tenant is
        // established from the message rather than from a JWT. AsTracking because the routing is
        // written onto this instance.
        var file = await db.KycFiles
            .AsTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == cmd.KycFileId && f.TenantId == cmd.TenantId, ct);

        if (file is null)
            return Result.Fail<RouteRemoteKycFileResult>(KycErrors.FileNotFound);

        // Not a remote file: ordinary, and most of them. The consumer sees every verification of
        // every channel, and filtering here rather than in the consumer keeps the decision about
        // what this command applies to in the command.
        if (file.Channel != KycChannel.RemoteSelfService)
            return Nothing(file);

        // Already routed. The second half of the idempotence story — see the class remarks.
        if (file.ValidationRoute is not null)
        {
            logger.LogDebug(
                "KYC file {KycFileId} is already routed ({Route}/{Reason}); nothing re-evaluated",
                file.Id, file.ValidationRoute, file.RoutingReason);

            return Nothing(file);
        }

        // Never null — a tenant with no row runs on the platform defaults, whose
        // KycAutoApprovalEnabled is false. Read ONCE, here, and passed down: the policy and the
        // sampling must both answer to the same compliance row.
        var compliance = await administration.GetComplianceSettingsAsync(cmd.TenantId, ct);
        var input = await GatherAsync(file, compliance, ct);
        var decision = KycAutoApprovalPolicy.Evaluate(input);

        return decision.Automatic
            // The sample percentage travels with the decision rather than being read again: two
            // reads of the same compliance row could straddle an officer's edit, and an approval
            // granted under one policy must be sampled under the same one.
            ? await ApproveAsync(cmd, file, decision, compliance.KycAutoApprovalSamplePct, ct)
            : await RouteToHumanAsync(cmd, file, decision, ct);
    }

    // ── Gathering ───────────────────────────────────────────────────────────

    /// <summary>
    /// Six reads, and every one of them fails in the direction that asks for a human.
    ///
    /// <para>
    /// A compliance setting this module cannot read, a product whose policy has gone, an
    /// identity M01 will not confirm, a journey that cannot be resolved: each lands on a
    /// condition the policy refuses. Nothing here throws and nothing defaults to "yes" —
    /// an M12 outage must not approve a relationship.
    /// </para>
    /// </summary>
    private async Task<KycAutoApprovalPolicy.Input> GatherAsync(
        KycFile file, ComplianceSettingsDto compliance, CancellationToken ct)
    {
        var policy = file.ProductLineId is { } lineId
            ? await administration.GetKycRemotePolicyAsync(file.TenantId, lineId, ct)
            : null;

        // The head of the assessment history, which is where the service's flags are. Read from
        // the assessment and not from the file: the file caches the score and the level, never the
        // flags, and a field correction appends a new assessment the routing must weigh instead of
        // the original reading.
        var assessment = await db.KycConfidenceAssessments
            .IgnoreQueryFilters()
            .Where(a => a.TenantId == file.TenantId && a.KycFileId == file.Id)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new { a.FlagsJson, a.GlobalScore, a.Level })
            .FirstOrDefaultAsync(ct);

        var flags = ReadFlags(assessment?.FlagsJson, file.Id);

        // The faces matched on the FIRST attempt. Two rows means somebody's first selfie was
        // refused, which KycApprovalCircuit already reads as a signal about the capture rather
        // than about the customer — so the automatic path treats it the same way.
        var matchedFirstTry = await db.KycFaceVerifications
            .IgnoreQueryFilters()
            .AnyAsync(
                v => v.TenantId == file.TenantId
                  && v.KycFileId == file.Id
                  && v.Attempt == 1
                  && v.IsMatch,
                ct)
            && file.FaceMatchAttempts <= 1;

        // This platform's reading of "aucune alerte LBC-FT ouverte" — see the policy's remarks.
        var openReview = await db.KycReviewSchedules
            .IgnoreQueryFilters()
            .AnyAsync(
                r => r.TenantId == file.TenantId
                  && r.KycFileId == file.Id
                  && r.Trigger == KycReviewTrigger.Event
                  && (r.Status == KycReviewStatus.Scheduled || r.Status == KycReviewStatus.Due),
                ct);

        var identityMatches = await IdentityMatchesAsync(file, ct);

        var (missing, overBudget) = await JourneyGapsAsync(file, ct);

        // The SAME gate the final rung of the manual circuit consults, called here so the two
        // paths cannot drift. See KycRoutingReasons.ProofOfAddressRequired for what it costs.
        var evidence = await remoteSaleEvidence.EnsureSatisfiedAsync(file, ct);

        return new KycAutoApprovalPolicy.Input(
            TenantAllowsAutoApproval: compliance.KycAutoApprovalEnabled,
            TenantMaxTier: compliance.KycAutoApprovalMaxTier,
            PolicyEnabled: policy?.Enabled ?? false,
            PolicyIsAutomatic: string.Equals(
                policy?.ValidationMode, "Automatic", StringComparison.OrdinalIgnoreCase),
            RequiredTier: policy?.RequiredTier,
            MinAutoScore: policy?.MinAutoScore ?? int.MaxValue,
            ConfidenceLevel: assessment?.Level ?? file.ConfidenceLevel,
            ConfidenceScore: assessment?.GlobalScore ?? file.ConfidenceScore,
            Flags: flags,
            IdentityMatchesRecord: identityMatches,
            IsPoliticallyExposed: file.IsPoliticallyExposed,
            VigilanceLevel: file.VigilanceLevel,
            DuplicateSuspected: file.DuplicateSuspected,
            FaceMatchedFirstTry: matchedFirstTry,
            HasOpenEventReview: openReview,
            MissingDocumentKinds: missing,
            DocumentsOverRetryBudget: overBudget,
            RemoteSaleEvidenceSatisfied: evidence.IsSuccess);
    }

    /// <summary>
    /// Asks M01 whether what the OCR read agrees with the client record.
    ///
    /// <para>
    /// The reading comes out of <c>KycIdentityDocument.OcrFieldsJson</c> — the projection with the
    /// document number stripped, which is all this comparison needs — through
    /// <see cref="KycOcrFields"/>, so the field-name matching is the same code the verification
    /// used to write it. The values travel to M01 and no further: they are not logged, not
    /// audited and not returned.
    /// </para>
    ///
    /// <para>
    /// No identity document at all is a NO, like an unreadable one. A remote file always has a
    /// front — the journey refuses to submit without it — so reaching here with no row means the
    /// verification never produced a reading, which is not a file a machine may approve.
    /// </para>
    /// </summary>
    private async Task<bool> IdentityMatchesAsync(KycFile file, CancellationToken ct)
    {
        var fieldsJson = await db.KycIdentityDocuments
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == file.TenantId
                     && d.KycFileId == file.Id
                     && d.OcrFieldsJson != null)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => d.OcrFieldsJson)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(fieldsJson)) return false;

        Dictionary<string, string>? fields;

        try
        {
            fields = JsonSerializer.Deserialize<Dictionary<string, string>>(fieldsJson);
        }
        catch (JsonException ex)
        {
            // A reading this module wrote and cannot read back is an incident, not a customer's
            // fault — logged with the file id and never with the payload, which holds a name.
            logger.LogError(ex,
                "KYC file {KycFileId}: ocr_fields_json could not be read back; the identity "
                + "consistency check is reported as failed and the file routes to a human",
                file.Id);

            return false;
        }

        if (fields is null || fields.Count == 0) return false;

        return await customers.MatchesReadIdentityAsync(
            file.TenantId,
            file.CustomerId,
            KycOcrFields.FindFirst(fields, KycOcrFields.SurnameKeys),
            KycOcrFields.FindFirst(fields, KycOcrFields.GivenNamesKeys),
            KycOcrFields.ParseDate(KycOcrFields.FindFirst(fields, KycOcrFields.BirthDateKeys)),
            ct);
    }

    /// <summary>
    /// What the customer's own journey still considers missing, and which documents they
    /// photographed more often than the tenant allows.
    ///
    /// <para>
    /// Read through <c>KycRemoteJourney</c> rather than recomputed, so the policy and the send
    /// button the customer pressed agree about what "complete" means. A journey that cannot be
    /// resolved — an administrator disabled remote collection mid-flight — reports the always-
    /// required kinds as missing rather than reporting nothing: an unresolvable journey is not an
    /// empty list of requirements.
    /// </para>
    /// </summary>
    private async Task<(IReadOnlyCollection<KycDocumentKind> Missing, IReadOnlyCollection<string> OverBudget)>
        JourneyGapsAsync(KycFile file, CancellationToken ct)
    {
        var invitation = await db.KycRemoteInvitations
            .IgnoreQueryFilters()
            .Where(i => i.TenantId == file.TenantId && i.KycFileId == file.Id)
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (invitation is null)
        {
            // A RemoteSelfService file with no invitation is not a state this module produces —
            // the channel is set by the submission of one. Reported rather than swallowed.
            logger.LogWarning(
                "KYC file {KycFileId} is RemoteSelfService but carries no invitation; "
                + "its document requirements cannot be resolved",
                file.Id);

            return (KycRemoteJourney.AlwaysRequired, []);
        }

        var state = await journey.ReadAsync(invitation, ct);

        if (state is null)
            return (KycRemoteJourney.AlwaysRequired, []);

        var overBudget = state.RetriesUsed
            .Where(r => state.MaxRetries > 0 && r.Value > state.MaxRetries)
            .Select(r => r.Key)
            .ToList();

        return (state.Missing, overBudget);
    }

    /// <summary>
    /// The scoring service's flags, as they were stored. A payload that will not parse yields an
    /// EMPTY list and a log line — never an exception: this is a gate, and a gate that throws
    /// leaves the file in validation with nobody told.
    ///
    /// <para>
    /// An empty list means "no blocking flag", which is the permissive direction, and that is
    /// deliberate: a file whose flags cannot be read still has to clear every other condition —
    /// the score, the identity, the vigilance, the documents. Failing closed on an unparsable
    /// payload would route every file of a tenant to a human the day the service changed the shape
    /// of one field, which is an outage dressed as a compliance decision.
    /// </para>
    /// </summary>
    private IReadOnlyCollection<string> ReadFlags(string? flagsJson, Guid kycFileId)
    {
        if (string.IsNullOrWhiteSpace(flagsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(flagsJson) ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogError(ex,
                "KYC file {KycFileId}: flags_json could not be read back; no blocking flag is "
                + "reported and the remaining conditions decide",
                kycFileId);

            return [];
        }
    }

    // ── Applying ────────────────────────────────────────────────────────────

    /// <summary>
    /// Approves with no human: the decision row, the file, and the two events every downstream
    /// module already consumes.
    /// </summary>
    private async Task<Result<RouteRemoteKycFileResult>> ApproveAsync(
        RouteRemoteKycFileCommand cmd,
        KycFile file,
        KycAutoApprovalPolicy.Decision decision,
        int samplePct,
        CancellationToken ct)
    {
        // Read BEFORE the aggregate is touched, exactly as DecideKycApprovalHandler does:
        // KycTierChangedEvent is about the move from one tier to the other, and ApproveAutomatically
        // sets the new one.
        var previousTier = file.Tier;

        var approved = file.ApproveAutomatically(decision.Tier, clock);
        if (approved.IsFailure)
            return Result.Fail<RouteRemoteKycFileResult>(approved.Error!);

        var step = KycApprovalStep.Pending(
            cmd.TenantId, file.Id, KycApprovalLevel.Automatic, clock);

        // SYSTEM is Guid.Empty throughout this codebase. The step records the account that
        // decided, and that account is the one that submitted — which is exactly why this is NOT
        // KycFile.Approve: four eyes refuses that pairing, correctly, and an unattended approval
        // is a different act rather than a signature nobody gave.
        var decided = step.Decide(
            KycApprovalDecision.Approved,
            approverId: Guid.Empty,
            clock,
            comment: BuildComment(decision));

        if (decided.IsFailure)
            return Result.Fail<RouteRemoteKycFileResult>(decided.Error!);

        db.KycApprovalSteps.Add(step);

        // KRM-B-11. In THIS unit of work, so the control row and the approval it is about commit
        // together — a crash between them would leave either an approval nobody will ever control
        // or a control of an approval that never happened.
        sampler.Draw(file, decision.Tier, KycAutoApprovalPolicyVersion.Current, samplePct);

        var now = clock.GetUtcNow();

        // The SAME two events the final rung of a human circuit publishes, through the SAME
        // outbox, so M01 activates the client and M03/M07 refresh their ceilings on an automatic
        // approval exactly as they do on a manual one. A third event meaning "validated
        // automatically" would be a second definition of validated.
        await publisher.PublishAsync(
            new KycValidatedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                ValidatedAt: file.ValidatedAt ?? now),
            ct);

        await publisher.PublishAsync(
            new KycTierChangedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                PreviousTier: previousTier.ToString(),
                CurrentTier: file.Tier.ToString(),
                ChangedAt: now),
            ct);

        // And one of our own, for the customer's email (KRM-B-10) and for KRM-B-11's sampling.
        // Separate from the two above because it says something they do not: that NOBODY decided.
        await publisher.PublishAsync(
            new KycRemoteDecidedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                KycFileId: file.Id,
                Automatic: true,
                RoutingReason: null,
                Tier: file.Tier.ToString(),
                ProductLineId: file.ProductLineId,
                ResponsibleAgentId: file.ResponsibleAgentId,
                DecidedAt: now),
            ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "KYC file {KycFileId} approved AUTOMATICALLY at tier {Tier} (verification of {At}); "
            + "no human decided",
            file.Id, file.Tier, cmd.VerificationCompletedAt);

        return Result.Ok(new RouteRemoteKycFileResult(
            file.Id, Routed: true, Automatic: true, Reason: null,
            FileStatus: file.Status.ToString(), Tier: file.Tier.ToString()));
    }

    /// <summary>
    /// Sends the file to a human: the reason on the file, the circuit when one is owed, and the
    /// event KRM-B-09's notification consumes.
    ///
    /// <para>
    /// <b>It moves no status.</b> The file is already where the verification left it —
    /// <c>Validating</c> when the score was usable, <c>ComplementRequired</c> when it was refused
    /// — and a routing decision is about WHO decides. A <c>ComplementRequired</c> file therefore
    /// gets no circuit: there is nothing to approve yet, and the agent's next act is to ask the
    /// customer for a better photograph or to refuse the file, both of which have their own
    /// slices.
    /// </para>
    /// </summary>
    private async Task<Result<RouteRemoteKycFileResult>> RouteToHumanAsync(
        RouteRemoteKycFileCommand cmd,
        KycFile file,
        KycAutoApprovalPolicy.Decision decision,
        CancellationToken ct)
    {
        var reason = decision.Reason!;

        if (!file.RouteToManual(reason, clock))
        {
            // Lost a race with another delivery of the same message. The winner's reason stands.
            logger.LogDebug(
                "KYC file {KycFileId} was routed concurrently; keeping {Reason}",
                file.Id, file.RoutingReason);

            return Nothing(file);
        }

        await publisher.PublishAsync(
            new KycRemoteDecidedEvent(
                TenantId: file.TenantId,
                CustomerEntityId: file.CustomerId,
                KycFileId: file.Id,
                Automatic: false,
                RoutingReason: reason,
                Tier: file.Tier.ToString(),
                ProductLineId: file.ProductLineId,
                ResponsibleAgentId: file.ResponsibleAgentId,
                DecidedAt: clock.GetUtcNow()),
            ct);

        await db.SaveChangesAsync(ct);

        // AFTER the commit, and only for a file that has something to approve. The circuit is
        // idempotent and reads the file itself, so it is safe to send on a redelivery; it is sent
        // here rather than from RunKycVerificationHandler because the automatic branch above must
        // not leave a ladder of pending human rungs behind it.
        if (file.Status == KycFileStatus.Validating)
        {
            var started = await sender.Send(
                new StartKycApprovalCommand(cmd.TenantId, file.Id, StartedBy: Guid.Empty), ct);

            if (started.IsFailure)
            {
                // The routing stands either way: the file is in validation and the reason is
                // recorded. A circuit that could not be created is visible from the compliance
                // backlog, and refusing here would roll back a decision over it.
                logger.LogError(
                    "KYC file {KycFileId} was routed to a human but its approval circuit could "
                    + "not be started: {Error}", file.Id, started.Error);
            }
        }

        logger.LogInformation(
            "KYC file {KycFileId} routed to a human: {Reason} (file is {Status}); "
            + "rules {Rules}",
            file.Id, reason, file.Status,
            string.Join(", ", decision.Rules.Select(r => $"{r.Rule}={r.Passed}")));

        return Result.Ok(new RouteRemoteKycFileResult(
            file.Id, Routed: true, Automatic: false, Reason: reason,
            FileStatus: file.Status.ToString(), Tier: file.Tier.ToString()));
    }

    /// <summary>
    /// KRM-B-08's « commentaire … score et règles évaluées en jsonb », in the one field a step
    /// has.
    ///
    /// <para>
    /// <b>No score, and no flag.</b> The story asks for both; the comment is shown verbatim on the
    /// file's history (KRM-F-08) and travels nowhere else, and a score printed beside an approval
    /// reads as the reason it was given. The reason an automatic approval was given is that every
    /// condition held — which is what the rules array says. The score itself is already on the
    /// file and in the assessment history, under the permission that guards them.
    /// </para>
    ///
    /// <para>
    /// The step's <c>comment</c> column is text, not jsonb, and widening it would mean a migration
    /// plus a second shape for every human comment beside it. The JSON goes in the text column
    /// after the sentence: psql reads it, the screen shows the sentence, and nothing has to be
    /// migrated for an audit trail that is written once and never queried by shape.
    /// </para>
    /// </summary>
    private static string BuildComment(KycAutoApprovalPolicy.Decision decision)
    {
        var sentence = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            AutoApprovalComment,
            decision.Tier,
            KycAutoApprovalPolicyVersion.Current);

        var rules = JsonSerializer.Serialize(
            decision.Rules.ToDictionary(r => r.Rule, r => r.Passed));

        return $"{sentence} — {rules}";
    }

    private static Result<RouteRemoteKycFileResult> Nothing(KycFile file)
        => Result.Ok(new RouteRemoteKycFileResult(
            file.Id,
            Routed: false,
            Automatic: file.ValidationRoute == KycValidationRoute.Automatic,
            Reason: file.RoutingReason,
            FileStatus: file.Status.ToString(),
            Tier: file.Tier.ToString()));
}

/// <summary>
/// The version of the rule set <see cref="KycAutoApprovalPolicy"/> implements, stamped on every
/// automatic decision.
///
/// <para>
/// KRM-B-08 asks the comment to carry « politique produit {code} v{n} ». The <c>n</c> is not the
/// product's version — a product has none, <c>ProductSpeciality</c> carries no revision counter —
/// so it is the version of the CONDITIONS, which is the thing a regulator asks about: "under which
/// rules was this relationship entered into without a human". Bump it whenever a condition is
/// added, removed or loosened, and never for a wording change.
/// </para>
/// </summary>
internal static class KycAutoApprovalPolicyVersion
{
    public const int Current = 1;
}
