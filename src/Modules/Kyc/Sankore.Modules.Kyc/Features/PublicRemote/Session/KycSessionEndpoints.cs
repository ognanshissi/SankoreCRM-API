namespace Sankore.Modules.Kyc.Features.PublicRemote.Session;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Features.PublicRemote;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Kernel;

/// <summary>
/// The customer's own journey: what is left to do, consent, upload, retake, send
/// (KRM-B-06, KRM-B-07).
///
/// <para>
/// <b>Nothing here answers with an identity.</b> Not a name, not a field the OCR read, not a score,
/// not a face-match verdict. The customer is told whether a photograph is usable and what to fix —
/// never what the document says about them, which they can read themselves, and never how well
/// they matched it, which is the institution's judgement to make and the validator's to see.
/// </para>
///
/// <para>
/// <b>Thin endpoints rather than MediatR slices, and for the same reason as the identification
/// endpoint.</b> <c>AuditBehavior</c> serialises a command's whole payload into the audit trail,
/// and these payloads are photographs. The one write that genuinely belongs in a command — the
/// verification — is dispatched as one from the submission, where it carries storage references
/// and no bytes.
/// </para>
/// </summary>
internal static class KycSessionEndpoints
{
    internal static IEndpointRouteBuilder MapKycRemoteSessionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("session")
            .WithTags("KYC public")
            // THE policy that separates this surface from the back office: its own scheme, its own
            // signing key, its own audience. A platform token presented here does not validate,
            // and a session token presented anywhere else does not either.
            .RequireAuthorization(KycRemoteSessionClaims.SchemeName);

        group.MapGet("", GetAsync)
            .WithName("GetKycRemoteSession")
            .WithSummary("What the customer still has to do")
            .WithDescription(
                "The identity documents this product accepts, the documents it requires, what has "
                + "already been uploaded, and how many retakes are left per document. Carries the "
                + "consent text and its version. No identity data of any kind.")
            .Produces<KycSessionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("consent", ConsentAsync)
            .WithName("GiveKycRemoteConsent")
            .WithSummary("Record the customer's consent to biometric processing")
            .WithDescription(
                "Mandatory before any upload. The version of the text shown must be the current "
                + "one — a page loaded before a deployment is sent a reload rather than having its "
                + "agreement recorded against wording it never displayed. Recording it twice does "
                + "not move the timestamp.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("documents", UploadAsync)
            .WithName("UploadKycRemoteDocument")
            .WithSummary("Send one photograph and get an immediate quality verdict")
            .WithDescription(
                "multipart/form-data. `kind` is the document being sent and `documentType` the "
                + "identity document the customer chose, required for the front and the back. "
                + "A photograph the service cannot read is REFUSED with the causes — blurred, "
                + "under-exposed, glare — and does not count as uploaded, so the customer retakes "
                + "it rather than discovering at the end that their file is unreadable. Past the "
                + "tenant's retake budget the upload is accepted anyway and flagged for the "
                + "validator: a customer whose camera the service simply dislikes must not be "
                + "stranded. Nothing the OCR read is ever returned.")
            .Produces<KycUploadResponse>(StatusCodes.Status200OK)
            .Produces<KycUploadResponse>(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .DisableAntiforgery()
            .WithOpenApi();

        group.MapDelete("documents/{kind}", DeleteAsync)
            .WithName("DeleteKycRemoteDocument")
            .WithSummary("Drop a photograph so it can be retaken")
            .WithDescription(
                "Before submission only. Removes the encrypted object and its registry row so the "
                + "recap screen shows the document as missing again.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        group.MapPost("submit", SubmitAsync)
            .WithName("SubmitKycRemoteSession")
            .WithSummary("Send the file for verification")
            .WithDescription(
                "Refused with the list of what is missing when a required document is absent. "
                + "Consumes the link, ends the session, marks the file as remotely self-collected "
                + "and runs the verification under the SYSTEM account — so no agent is recorded as "
                + "having produced this evidence, which is what leaves every agent free to decide "
                + "on it. The response is the same whatever the product's validation mode: the "
                + "customer is told their file was sent, never how it will be judged.")
            .Produces<KycSubmitResponse>(StatusCodes.Status200OK)
            .Produces<KycSubmitRefusal>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return app;
    }

    // ── Reading the journey ──────────────────────────────────────────────────

    private static async Task<IResult> GetAsync(
        KycRemoteGate gate,
        KycRemoteJourney journey,
        ITenantStore tenants,
        CancellationToken ct)
    {
        var resolution = await gate.ResolveSessionAsync(ct);
        if (Reject(resolution) is { } rejection) return rejection;

        var invitation = resolution.Invitation!;

        var state = await journey.ReadAsync(invitation, ct);
        if (state is null) return Gone();

        var tenant = await tenants.GetAsync(invitation.TenantId, ct);

        return Results.Ok(new KycSessionResponse(
            AllowedDocumentTypes: [.. state.AllowedDocumentTypes.Select(t => t.ToString())],
            RequiredDocuments: [.. state.RequiredKinds.Select(k => k.ToString())],
            UploadedDocuments: [.. state.Uploaded.Where(p => p.Value).Select(p => p.Key.ToString())],
            MissingDocuments: [.. state.Missing.Select(k => k.ToString())],
            ChosenDocumentType: state.ChosenType?.ToString(),
            ConsentGiven: state.HasConsented,
            ConsentTextVersion: KycRemoteConsentText.CurrentVersion,
            ConsentText: KycRemoteConsentText.For(tenant?.DefaultLanguage),
            RetriesRemaining: state.RequiredKinds
                .Concat([KycDocumentKind.IdentityDocumentBack])
                .Distinct()
                .ToDictionary(
                    k => k.ToString(),
                    k => Math.Max(0, state.MaxRetries - state.RetriesUsed.GetValueOrDefault(k.ToString()))),
            ExpiresAt: invitation.ExpiresAt));
    }

    // ── Consent ──────────────────────────────────────────────────────────────

    private static async Task<IResult> ConsentAsync(
        KycConsentRequest request,
        KycRemoteGate gate,
        KycDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var resolution = await gate.ResolveSessionAsync(ct);
        if (Reject(resolution) is { } rejection) return rejection;

        if (!KycRemoteConsentText.IsKnownVersion(request?.TextVersion))
        {
            // The page is stale. Told apart from every other refusal because the fix is a reload,
            // which the front can do by itself.
            return Results.BadRequest(new
            {
                error = "KYC_REMOTE_CONSENT_VERSION_STALE",
                currentVersion = KycRemoteConsentText.CurrentVersion,
            });
        }

        var invitation = resolution.Invitation!;

        var consented = invitation.RecordConsent(request!.TextVersion!, clock);
        if (consented.IsFailure)
            return Results.BadRequest(new { error = consented.Error });

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── Upload, with the quality gate ────────────────────────────────────────

    private static async Task<IResult> UploadAsync(
        KycDocumentKind kind,
        KycDocumentType? documentType,
        IFormFile file,
        KycRemoteGate gate,
        KycRemoteJourney journey,
        KycRemoteQualityGate quality,
        KycDbContext db,
        IKycDocumentStore store,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(KycSessionEndpoints));

        var resolution = await gate.ResolveSessionAsync(ct);
        if (Reject(resolution) is { } rejection) return rejection;

        var invitation = resolution.Invitation!;

        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "KYC_DOCUMENT_EMPTY" });

        var state = await journey.ReadAsync(invitation, ct);
        if (state is null) return Gone();

        // The consent is the lawful basis for everything that follows, so it gates the FIRST
        // upload rather than the submission. Refusing at the end would mean having stored a
        // biometric photograph without one.
        if (!state.HasConsented)
            return Results.BadRequest(new { error = KycErrors.RemoteConsentRequired });

        // The declared type is required for the two kinds the OCR needs a template for, and must
        // be one the PRODUCT accepts — the page offers that list, so a value outside it means the
        // list was bypassed.
        var needsType = kind is KycDocumentKind.IdentityDocumentFront
                             or KycDocumentKind.IdentityDocumentBack;

        if (needsType)
        {
            if (documentType is not { } declared
                || !state.AllowedDocumentTypes.Contains(declared))
            {
                return Results.BadRequest(new { error = KycErrors.RemoteDocumentTypeNotAllowed });
            }
        }

        // A selfie is quality-checked by comparing it with the document portrait, so there has to
        // be a document first. The journey presents them in that order; this is what holds when
        // somebody calls the endpoints directly.
        if (kind == KycDocumentKind.Selfie
            && !state.Uploaded.GetValueOrDefault(KycDocumentKind.IdentityDocumentFront))
        {
            return Results.BadRequest(new { error = KycErrors.RemoteIdentityDocumentFirst });
        }

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        // Checked BEFORE anything is stored. A photograph the service cannot read must not become
        // a row the customer then has to delete — and must not consume storage either.
        var verdict = await quality.InspectAsync(invitation, kind, bytes, file.ContentType, ct);

        if (verdict.Refused)
        {
            var used = invitation.RecordQualityRetry(kind, clock);
            await db.SaveChangesAsync(ct);

            var remaining = Math.Max(0, state.MaxRetries - used);

            logger.LogInformation(
                "Remote upload of {Kind} refused for KYC file {KycFileId}: {Code} "
                + "({Remaining} retake(s) left)",
                kind, invitation.KycFileId, verdict.Code, remaining);

            return Results.Json(
                new KycUploadResponse(
                    Accepted: false,
                    Kind: kind.ToString(),
                    QualityCode: verdict.Code,
                    QualityCauses: verdict.Causes,
                    RetriesRemaining: remaining),
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        try
        {
            await using var content = new MemoryStream(bytes, writable: false);

            var stored = await store.StoreAsync(
                invitation.TenantId, invitation.KycFileId, kind, content, file.ContentType, ct);

            var document = KycDocument.Register(
                tenantId: invitation.TenantId,
                kycFileId: invitation.KycFileId,
                kind: kind,
                storageRef: stored.StorageRef,
                contentType: stored.ContentType,
                sizeBytes: stored.SizeBytes,
                sha256: stored.Sha256,
                // The INVITATION, never a back-office user. KRM-B-06 is explicit about it, and the
                // reason is the four-eyes rule: an employee recorded as having uploaded this image
                // would be barred from deciding on the file, and no employee touched it.
                uploadedBy: invitation.Id,
                clock: clock,
                declaredDocumentType: documentType);

            db.KycDocuments.Add(document);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // Object written before the row and this is not a command, so there is no ambient
                // transaction: a failed save would leave an encrypted object nothing points at.
                // Same ordering and same cleanup as the agent's upload endpoint.
                try
                {
                    await store.DeleteAsync(
                        invitation.TenantId, stored.StorageRef, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    logger.LogError(cleanupFailure,
                        "Could not remove the orphaned remote KYC object {StorageRef}",
                        stored.StorageRef);
                }

                throw;
            }

            logger.LogInformation(
                "Remote upload accepted for KYC file {KycFileId}: {Kind}, {Bytes} bytes",
                invitation.KycFileId, kind, stored.SizeBytes);

            return Results.Ok(new KycUploadResponse(
                Accepted: true,
                Kind: kind.ToString(),
                QualityCode: null,
                QualityCauses: [],
                // The budget is untouched by an accepted photograph.
                RetriesRemaining: Math.Max(
                    0, state.MaxRetries - state.RetriesUsed.GetValueOrDefault(kind.ToString()))));
        }
        catch (DomainException ex)
        {
            // The store refuses an unsupported content type and an oversized payload with stable
            // KYC_DOCUMENT_* codes. A phone sending a 20 MB photograph is the caller's problem to
            // be told about, not a 500.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // ── Retake ───────────────────────────────────────────────────────────────

    private static async Task<IResult> DeleteAsync(
        KycDocumentKind kind,
        KycRemoteGate gate,
        KycDbContext db,
        IKycDocumentStore store,
        CancellationToken ct)
    {
        var resolution = await gate.ResolveSessionAsync(ct);
        if (Reject(resolution) is { } rejection) return rejection;

        var invitation = resolution.Invitation!;

        // Only what the CUSTOMER sent through this link. An agent's earlier attachment on the same
        // file is not theirs to remove, and `uploaded_by = invitation id` is exactly what tells
        // the two apart.
        var documents = await db.KycDocuments
            .IgnoreQueryFilters()
            .AsTracking()
            .Where(d => d.TenantId == invitation.TenantId
                     && d.KycFileId == invitation.KycFileId
                     && d.Kind == kind
                     && d.UploadedBy == invitation.Id)
            .ToListAsync(ct);

        if (documents.Count == 0)
            return Results.NotFound(new { error = KycErrors.DocumentNotFound });

        db.KycDocuments.RemoveRange(documents);
        await db.SaveChangesAsync(ct);

        // The objects go AFTER the rows are gone. The other order would leave a row pointing at
        // nothing, which the recap screen would advertise as a photograph that 404s; an orphaned
        // object is invisible and swept by retention.
        foreach (var document in documents)
        {
            try
            {
                await store.DeleteAsync(invitation.TenantId, document.StorageRef, ct);
            }
            catch (Exception)
            {
                // Swallowed: the customer's retake must not fail because the store could not
                // delete. The row is already gone, which is what the journey reads.
            }
        }

        return Results.NoContent();
    }

    // ── Submission ───────────────────────────────────────────────────────────

    private static async Task<IResult> SubmitAsync(
        KycRemoteGate gate,
        KycRemoteJourney journey,
        KycRemoteSubmission submission,
        CancellationToken ct)
    {
        var resolution = await gate.ResolveSessionAsync(ct);
        if (Reject(resolution) is { } rejection) return rejection;

        var invitation = resolution.Invitation!;

        var state = await journey.ReadAsync(invitation, ct);
        if (state is null) return Gone();

        if (!state.HasConsented)
            return Results.BadRequest(new { error = KycErrors.RemoteConsentRequired });

        if (state.Missing.Count > 0)
        {
            return Results.BadRequest(new KycSubmitRefusal(
                Error: KycErrors.RemoteDocumentsMissing,
                MissingDocuments: [.. state.Missing.Select(k => k.ToString())]));
        }

        var submitted = await submission.SubmitAsync(invitation, state, ct);

        return submitted.IsSuccess
            // The SAME answer whatever the product's validation mode, and whatever the verification
            // then concludes. KRM-F-05 asks for one final screen; the reason is that a customer told
            // "validated instantly" would read a refusal two minutes later as the institution
            // changing its mind.
            ? Results.Ok(new KycSubmitResponse(Submitted: true))
            : Results.Json(
                new { error = submitted.Error }, statusCode: StatusCodes.Status409Conflict);
    }

    // ── Shared refusals ──────────────────────────────────────────────────────

    /// <summary>
    /// Turns a gate verdict into the response every session endpoint owes, or null when the
    /// session is good.
    ///
    /// <para>
    /// Shaped once so five endpoints cannot drift into five slightly different answers — which is
    /// how one of them ends up saying more than the others.
    /// </para>
    /// </summary>
    private static IResult? Reject(KycRemoteGate.LinkResolution resolution) => resolution.State switch
    {
        KycRemoteGate.LinkState.Usable => null,

        KycRemoteGate.LinkState.WrongTenant => Results.StatusCode(StatusCodes.Status403Forbidden),

        // 409 and not 401: the session is cryptographically fine, the LINK behind it is spent or
        // withdrawn. A 401 would send the page to the identification screen, where the customer
        // would try again and fail for a reason nobody explained.
        KycRemoteGate.LinkState.Consumed
            or KycRemoteGate.LinkState.Revoked
            or KycRemoteGate.LinkState.Locked
            or KycRemoteGate.LinkState.Expired
            or KycRemoteGate.LinkState.FileClosed
            => Results.Json(
                new { error = KycErrors.RemoteInvitationNotUsable, state = resolution.State.ToString() },
                statusCode: StatusCodes.Status409Conflict),

        _ => Results.StatusCode(StatusCodes.Status401Unauthorized),
    };

    /// <summary>
    /// The product stopped allowing remote collection while the customer was mid-journey. Its own
    /// answer because nothing the customer does can fix it: they finish at the branch.
    /// </summary>
    private static IResult Gone()
        => Results.Json(
            new { error = KycErrors.RemoteInvitationProductNotEligible },
            statusCode: StatusCodes.Status409Conflict);
}

/// <param name="AllowedDocumentTypes"><c>Cni</c> | <c>Passport</c> | <c>Cedeao</c> | <c>Consulaire</c>.</param>
/// <param name="RequiredDocuments">
/// <c>KycDocumentKind</c> names. The back appears only once the customer has chosen a type that has
/// one — never for a passport.
/// </param>
/// <param name="RetriesRemaining">Retakes left per document kind.</param>
/// <param name="ConsentText">
/// The exact wording to display, for <paramref name="ConsentTextVersion"/>. Served by the API so
/// the text the customer agreed to is the text the server can reproduce.
/// </param>
public sealed record KycSessionResponse(
    IReadOnlyList<string> AllowedDocumentTypes,
    IReadOnlyList<string> RequiredDocuments,
    IReadOnlyList<string> UploadedDocuments,
    IReadOnlyList<string> MissingDocuments,
    string? ChosenDocumentType,
    bool ConsentGiven,
    string ConsentTextVersion,
    string ConsentText,
    IReadOnlyDictionary<string, int> RetriesRemaining,
    DateTimeOffset ExpiresAt);

/// <param name="TextVersion">The version the page displayed. Must be the current one.</param>
public sealed record KycConsentRequest(string? TextVersion);

/// <param name="QualityCode">
/// The service's own code when the photograph was refused — <c>IMAGE_QUALITY_TOO_LOW</c>,
/// <c>DOCUMENT_UNREADABLE</c>, <c>NO_FACE_DETECTED</c>, <c>MULTIPLE_FACES_DETECTED</c>. Null when
/// accepted.
/// </param>
/// <param name="QualityCauses">
/// What to fix, in the service's own vocabulary (blur, exposure, glare). The front turns these into
/// one instruction; it never shows a code.
/// </param>
public sealed record KycUploadResponse(
    bool Accepted,
    string Kind,
    string? QualityCode,
    IReadOnlyList<string> QualityCauses,
    int RetriesRemaining);

/// <param name="MissingDocuments">What the customer still has to send.</param>
public sealed record KycSubmitRefusal(string Error, IReadOnlyList<string> MissingDocuments);

public sealed record KycSubmitResponse(bool Submitted);
