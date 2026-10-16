namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using System.Diagnostics;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Features.Telephony;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Listening to a recording, and taking one out of the platform (TEL-B-17).
///
/// <para>
/// <b>An authenticated stream and NOT a signed URL</b>, which departs from what the specification
/// suggested. The reason is the one <c>ReadKycDocumentEndpoint</c> already settled for identity
/// imagery: a signed URL lets the audit record only that a LINK was issued. The link then travels
/// by chat, is opened three days later by somebody who was never granted anything, and the log
/// says nothing about it. B-17 requires the LISTENING to be audited, and only a stream can be —
/// the bytes never leave a request carrying the reader's identity.
/// </para>
///
/// <para>
/// <b>Two permissions, because they are two acts.</b> <c>Telephony.Recording.Listen</c> plays it
/// inside the platform, under every control this endpoint applies. <c>Telephony.Recording.Export</c>
/// takes a copy out, past all of them — after which no control of ours exists — which is why the
/// export demands a stated reason and the listen does not.
/// </para>
///
/// <para>
/// <b>The permission is not the whole authorization, and on this surface that matters more than
/// anywhere else in the family.</b> What is served here is the actual audio of a conversation —
/// the most sensitive artefact telephony produces — so it carries the same rule the wrap-up and
/// the manual attachment do, widened by one step and with one escape:
/// </para>
/// <list type="bullet">
/// <item>your OWN call: always;</item>
/// <item>a call attributed to NOBODY: always, which is the agency's "to attribute" queue — the
/// same set the missed-call list deliberately shows everyone, because a voicemail left for the
/// branch has no owner and somebody has to be able to play it;</item>
/// <item>a COLLEAGUE's call: only when the record it is attached to is one you may work, asked of
/// the module that owns it (<see cref="TelephonyRecordAccess"/>). That is the step the wrap-up
/// does not need and this does: a branch manager reviewing their own agent's conversation with
/// their own branch's customer is a legitimate and common need, and "own calls only" would send
/// them to a compliance officer for it;</item>
/// <item>and <c>admin:compliance:manage</c> passes unconditionally, because a compliance officer
/// settling a dispute has to be able to hear a call outside every perimeter — which is precisely
/// the case the agency perimeter cannot express.</item>
/// </list>
///
/// <para>
/// <b>The audit trail is not a substitute for any of that.</b> <c>tel_recording_access_log</c>
/// exists so a disproportionate listener can be FOUND; detection and prevention are different
/// controls, and M01's reveal of a document number applies both for a datum that is arguably less
/// personal than somebody's recorded voice.
/// </para>
///
/// <para>
/// Both carry <c>.RequireRateLimiting("auth")</c>, the bucket the other sensitive reads use: a
/// script walking storage references is the threat this surface has, and it is cheap to make it
/// slow.
/// </para>
/// </summary>
internal static class RecordingAccessEndpoints
{
    internal static IEndpointRouteBuilder MapRecordingAccess(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("calls/{callId:guid}/recording", ListenAsync)
            .WithName("ListenTelephonyRecording")
            .WithSummary("Play a call's recording")
            .WithDescription(
                "Streams the decrypted audio and records WHO listened in "
                + "tel_recording_access_log, in the SAME transaction — so there is no window in "
                + "which a voice was heard without a trace. An authenticated stream and not a "
                + "signed link, because a link only ever proves that it was ISSUED. "
                + "Pass ?reason= when the caller stated one; it is optional here and mandatory on "
                + "the export. "
                + "404 for a call with no recording, 422 TELEPHONY_RECORDING_NOT_AVAILABLE when "
                + "the row exists but the bytes do not — pending retrieval, failed, or purged, "
                + "which are different answers from 'never existed'. "
                + "Requires permission: Telephony.Recording.Listen.")
            .RequireAuthorization(Permissions.CanListenTelephonyRecording.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader()
            .RequireRateLimiting("auth");

        app.MapGet("calls/{callId:guid}/recording/export", ExportAsync)
            .WithName("ExportTelephonyRecording")
            .WithSummary("Download a call's recording out of the platform")
            .WithDescription(
                "A different act from listening and a different permission: past this point no "
                + "control of ours applies to the file. The reason is MANDATORY (422 "
                + "TELEPHONY_RECORDING_EXPORT_REASON_REQUIRED) and is kept in "
                + "tel_recording_access_log — it is the only thing that makes the export "
                + "answerable afterwards. Served as an attachment. "
                + "Requires permission: Telephony.Recording.Export.")
            .RequireAuthorization(Permissions.CanExportTelephonyRecording.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader()
            .RequireRateLimiting("auth");

        app.MapPost("calls/{callId:guid}/recording/legal-hold", HoldAsync)
            .WithName("SetTelephonyRecordingLegalHold")
            .WithSummary("Freeze a recording against the retention purge, or release it")
            .WithDescription(
                "A hold suspends the RETENTION purge for a dispute. It is carried as a flag plus a "
                + "reason rather than by pushing the retention date out, because the original date "
                + "is the fact and the hold is the exception — merging them loses which was which. "
                + "It does NOT survive an anonymisation of the client: a right-to-erasure request "
                + "is a different instruction, and a hold placed years ago must not quietly veto "
                + "one. The reason is mandatory when placing a hold. Audited. "
                + "Requires permission: admin:compliance:manage.")
            .RequireAuthorization(Permissions.CanManageCompliance.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    /// <summary>
    /// Internal rather than private so a test can call it, which is how
    /// <c>ReadKycDocumentEndpoint.Handle</c> is reachable too: what these cases are about is what
    /// the ENDPOINT decides, and a test that re-implemented the branch would be testing its own
    /// copy of the rule.
    /// </summary>
    internal static Task<IResult> ListenAsync(
        Guid callId,
        string? reason,
        IntegrationDbContext db,
        // [FromServices] explicitly, and the reason is a start-up failure that only
        // `--emit-openapi` catches: a minimal-API parameter whose type is not a REGISTERED service
        // is inferred as the request BODY, and this one is registered only when an encryption key
        // is configured. On a deployment that records no calls the whole host then refuses to
        // start with "Body was inferred but the method does not allow inferred body parameters" —
        // invisible to the build and to every test.
        [FromServices] IRecordingStore? store,
        TelephonyRecordAccess recordAccess,
        TelephonyCallPerimeter perimeter,
        ICurrentUser currentUser,
        IHttpContextAccessor httpContextAccessor,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => ServeAsync(
            callId, reason, TelRecordingAccessKind.Listen,
            db, store, recordAccess, perimeter, currentUser, httpContextAccessor, clock,
            loggerFactory, ct);

    /// <inheritdoc cref="ListenAsync" />
    internal static async Task<IResult> ExportAsync(
        Guid callId,
        string? reason,
        IntegrationDbContext db,
        // [FromServices] explicitly, and the reason is a start-up failure that only
        // `--emit-openapi` catches: a minimal-API parameter whose type is not a REGISTERED service
        // is inferred as the request BODY, and this one is registered only when an encryption key
        // is configured. On a deployment that records no calls the whole host then refuses to
        // start with "Body was inferred but the method does not allow inferred body parameters" —
        // invisible to the build and to every test.
        [FromServices] IRecordingStore? store,
        TelephonyRecordAccess recordAccess,
        TelephonyCallPerimeter perimeter,
        ICurrentUser currentUser,
        IHttpContextAccessor httpContextAccessor,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // Enforced HERE and not on the aggregate: the log type's job is to record what happened,
        // and refusing to record an export that already happened would be the wrong place for it.
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Results.UnprocessableEntity(
                new { error = TelephonyErrors.RecordingExportReasonRequired });
        }

        return await ServeAsync(
            callId, reason, TelRecordingAccessKind.Export,
            db, store, recordAccess, perimeter, currentUser, httpContextAccessor, clock,
            loggerFactory, ct);
    }

    /// <summary>
    /// The body both share. One method, so the two cannot differ about what is checked before the
    /// bytes are handed over — which is the whole security story of this endpoint.
    /// </summary>
    private static async Task<IResult> ServeAsync(
        Guid callId,
        string? reason,
        TelRecordingAccessKind kind,
        IntegrationDbContext db,
        IRecordingStore? store,
        TelephonyRecordAccess recordAccess,
        TelephonyCallPerimeter perimeter,
        ICurrentUser currentUser,
        IHttpContextAccessor httpContextAccessor,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(RecordingAccessEndpoints));

        // The query filter scopes this to the caller's tenant: another tenant's call is absent, so
        // the answer is 404 and never 403 — the existence of a conversation must not leak.
        var recording = await db.TelRecordings
            .AsTracking()
            .FirstOrDefaultAsync(r => r.CallId == callId, ct);

        if (recording is null)
            return Results.NotFound(new { error = TelephonyErrors.RecordingNotFound });

        // ── Who may hear this ───────────────────────────────────────────────
        // Loaded after the recording so a call with none answers 404 without a perimeter lookup,
        // and the two 404s are indistinguishable: the existence of a conversation must not leak.
        var call = await db.TelCalls
            .Where(c => c.Id == callId)
            .Select(c => new { c.CrmUserId, c.EntityType, c.EntityId, c.AgencyId })
            .FirstOrDefaultAsync(ct);

        if (call is null)
            return Results.NotFound(new { error = TelephonyErrors.RecordingNotFound });

        if (!await MayHearAsync(call.CrmUserId, call.EntityType, call.EntityId, call.AgencyId,
                recordAccess, perimeter, currentUser, httpContextAccessor, ct))
        {
            // The refusal is logged — this surface is the one where a disproportionate listener
            // matters, and an attempt is as much a signal as an access. The access LOG is for
            // successes; a refusal never reaches it, so it would otherwise leave no trace at all.
            logger.LogWarning(
                "User {UserId} of tenant {TenantId} may not {Kind} the recording of call "
                + "{CallId}.", currentUser.Id, currentUser.TenantId, kind, callId);

            // 404 and not 403: a 403 confirms that a colleague had this conversation.
            return Results.NotFound(new { error = TelephonyErrors.RecordingNotFound });
        }

        if (recording.Status != TelRecordingStatus.Available
            || recording.StorageRef is not { } storageRef)
        {
            // Distinct from "not found" on purpose: "being fetched", "retrieval failed" and "gone
            // for ever" are all answers an agent looking for a recording needs, and a 404 for all
            // three would have them report a bug for a purge working as designed.
            return Results.UnprocessableEntity(
                new { error = TelephonyErrors.RecordingNotAvailable });
        }

        if (store is null)
        {
            logger.LogError(
                "A recording of call {CallId} is held but no store is registered — {Setting} is "
                + "probably not configured.",
                callId, $"{RecordingStorageOptions.SectionName}:EncryptionKey");

            return Results.UnprocessableEntity(
                new { error = TelephonyErrors.RecordingNotAvailable });
        }

        var bytes = await store.OpenAsync(currentUser.TenantId, storageRef, ct);

        if (bytes is null)
        {
            // The row says Available and the object is gone. NOT degraded to "not found": a
            // recording an institution believes it holds and does not is a fact somebody has to
            // see, not a quiet 404.
            logger.LogError(
                "Recording {RecordingId} of call {CallId} is marked Available but its object is "
                + "missing.", recording.Id, callId);

            return Results.UnprocessableEntity(
                new { error = TelephonyErrors.RecordingNotAvailable });
        }

        // The access row is written and committed BEFORE the bytes are returned. The same
        // discipline M01's reveal applies to a document number, for a datum that is arguably more
        // personal — and the ordering is what makes "no voice was heard without a trace" true
        // rather than approximately true.
        db.TelRecordingAccessLogs.Add(TelRecordingAccessLog.Record(
            tenantId: currentUser.TenantId,
            recordingId: recording.Id,
            callId: callId,
            actorUserId: currentUser.Id,
            kind: kind,
            accessedAt: clock.GetUtcNow(),
            reason: reason,
            // Ties this access to the HTTP request — and to the other reads of the same screen —
            // in Seq, without storing anything personal.
            correlationId: Activity.Current?.Id));

        await db.SaveChangesAsync(ct);

        // application/octet-stream and not the stored content type, deliberately: a browser handed
        // audio/mpeg on a GET will play it inline from a URL that is now in the history, and the
        // player the front end builds passes the bytes to an <audio> element itself. The export
        // adds a file name, which is what makes it a download.
        return kind == TelRecordingAccessKind.Export
            ? Results.File(bytes, "application/octet-stream", $"call-{callId:N}.audio")
            : Results.File(bytes, "application/octet-stream");
    }

    /// <summary>
    /// The listening rule, in one place so the listen and the export cannot differ about it.
    ///
    /// <para>
    /// Ordered cheapest first, and that is also the order of certainty: your own call is a field
    /// comparison, the compliance permission is a claim already on the token, and only the last
    /// two arms cost a lookup. An unattached, unattributed call falls to the first arm — the
    /// agency's "to attribute" queue, which the missed-call list shows everyone for the same
    /// reason.
    /// </para>
    ///
    /// <para>
    /// <b>The agency arm is TEL-B-19's and it closes a real gap.</b> Until the call carried an
    /// agency, the only route to a colleague's conversation was the record it was attached to —
    /// so a colleague's call that was never attached was audible to compliance alone. Correct, but
    /// strict enough that a branch manager investigating a complaint about a call their own agent
    /// made had no way to hear it. The agency on the row is the one its agent was in at the time,
    /// so the manager who may listen is the one who was answerable then.
    /// </para>
    ///
    /// <para>
    /// <b>It is an arm and not a replacement.</b> The attached-record arm stays, because a call
    /// filed against a client of another branch is a conversation that branch may legitimately
    /// need — and because a call with no agency (one opened while M12 was unreachable, or by an
    /// unlinked account) would otherwise lose its only route.
    /// </para>
    /// </summary>
    private static async Task<bool> MayHearAsync(
        Guid? crmUserId,
        string? entityType,
        Guid? entityId,
        Guid? agencyId,
        TelephonyRecordAccess recordAccess,
        TelephonyCallPerimeter perimeter,
        ICurrentUser currentUser,
        IHttpContextAccessor httpContextAccessor,
        CancellationToken ct)
    {
        // Mine, or nobody's.
        if (crmUserId is null || crmUserId == currentUser.Id) return true;

        // A compliance officer settling a dispute, who by definition works outside every agency
        // perimeter. Read off the token's own claims, which is how AuthorizationPolicies renders
        // every permission — so this cannot drift from what the policy would have checked.
        if (httpContextAccessor.HttpContext?.User.HasClaim(
                "permission", Permissions.CanManageCompliance.Code) == true)
        {
            return true;
        }

        // A colleague's call inside the perimeter this caller supervises — the branch the call
        // belonged to when it happened, not the one its agent sits in today.
        if (await perimeter.CoversAsync(currentUser.TenantId, currentUser.Id, agencyId, ct))
            return true;

        // Last: the record it is about decides. An unattached call with no agency does not reach
        // here with a route — it has an owner, that owner is not this caller, and nothing else
        // places it.
        return entityType is not null
            && entityId is { } id
            && await recordAccess.CanActOnAsync(
                currentUser.TenantId, entityType, id, currentUser.Id, ct);
    }

    private static async Task<IResult> HoldAsync(
        Guid callId, SetLegalHoldRequest request, ISender sender, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await sender.Send(
            new SetRecordingLegalHoldCommand(callId, request.Hold, request.Reason), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error == TelephonyErrors.RecordingNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.UnprocessableEntity(new { error = result.Error });
    }
}

/// <summary>
/// Freezes a recording against the retention purge, or releases it (TEL-B-17).
/// </summary>
internal sealed record SetRecordingLegalHoldCommand(Guid CallId, bool Hold, string? Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "TelephonyRecording";

    public string? ResourceId => CallId.ToString();
}

internal sealed class SetRecordingLegalHoldHandler(IntegrationDbContext db)
    : IRequestHandler<SetRecordingLegalHoldCommand, Result>
{
    public async Task<Result> Handle(SetRecordingLegalHoldCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (cmd.Hold && string.IsNullOrWhiteSpace(cmd.Reason))
            return Result.Fail(TelephonyErrors.RecordingHoldReasonRequired);

        var recording = await db.TelRecordings
            .AsTracking()
            .FirstOrDefaultAsync(r => r.CallId == cmd.CallId, ct);

        if (recording is null)
            return Result.Fail(TelephonyErrors.RecordingNotFound);

        // A hold on an already-purged recording is refused rather than recorded: the file is gone,
        // and a hold that looks placed would tell whoever asked for it that evidence is being kept.
        if (cmd.Hold && recording.Status == TelRecordingStatus.Purged)
            return Result.Fail(TelephonyErrors.RecordingNotAvailable);

        if (cmd.Hold) recording.PlaceLegalHold(cmd.Reason!);
        else recording.LiftLegalHold();

        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}

internal sealed record SetLegalHoldRequest(bool Hold, string? Reason);
