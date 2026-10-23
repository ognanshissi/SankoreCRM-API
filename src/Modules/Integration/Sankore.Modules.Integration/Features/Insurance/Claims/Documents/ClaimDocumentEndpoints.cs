namespace Sankore.Modules.Integration.Features.Insurance.Claims.Documents;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// The <i>pièces de sinistre</i> of one dossier: deposit, list, read back.
///
/// <para>
/// <b>Thin endpoints and not MediatR slices</b>, the same choice M02's KYC upload makes and for
/// the same reason: only METADATA is persisted here, and giving the upload a command would put the
/// image BYTES through the audit pipeline, which serialises what it is given. The acts that change
/// a dossier's state — the declaration, the indemnity — are the commands.
/// </para>
///
/// <para>
/// <b>There is no DELETE.</b> A piece is evidence ASS-12 names: an agent who uploaded the wrong
/// scan uploads the right one, and the wrong one stays on the dossier with its own digest and its
/// own upload timestamp. The only thing that removes bytes is the retention purge.
/// </para>
/// </summary>
internal static class ClaimDocumentEndpoints
{
    internal static IEndpointRouteBuilder MapClaimDocuments(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/insurance/claims")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.Insurance);

        group.MapPost("{claimId:guid}/documents", Upload)
            .WithName("UploadClaimDocument")
            .WithSummary("Deposit a piece on a claim dossier")
            .WithDescription(
                "Stores the file encrypted and registers it PENDING the antivirus pass — a piece "
                + "is never born clean, and nothing reaches the insurer before the scan has run "
                + "on it. Accepts image/jpeg, image/png, image/heic and application/pdf; an "
                + "archive is deliberately refused, because its contents are what a scan would "
                + "have to walk and this platform does not. "
                + "`requestedByInsurer=true` marks the piece as a complement the insurer asked "
                + "for, which is what makes it transmitted automatically once it is clean. "
                + "The same file twice on one dossier is a 409 on its digest: two rows would be "
                + "two attachments on the insurer's side. "
                + "Requires permission: Ins.Claim.Declare.")
            .RequireAuthorization(Permissions.CanDeclareInsuranceClaim.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadClaimDocumentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        group.MapGet("{claimId:guid}/documents", List)
            .WithName("ListClaimDocuments")
            .WithSummary("The pieces of one dossier")
            .WithDescription(
                "Metadata only — the kind, the size, the digest, the scan verdict and whether the "
                + "piece has reached the insurer. `isTransmittable` is the aggregate's own answer, "
                + "so a screen does not re-derive the antivirus gate from a status name. "
                + "Requires permission: Ins.Claim.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceClaim.Code)
            .Produces<IReadOnlyList<ClaimDocumentDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{claimId:guid}/documents/{documentId:guid}/content", Download)
            .WithName("DownloadClaimDocument")
            .WithSummary("Read a piece back")
            .WithDescription(
                "Only a piece the antivirus has passed: handing an agent a file the scanner "
                + "flagged — or has not yet looked at — is the one thing the lock exists to "
                + "prevent, so a Pending or Infected piece is a 409 rather than a download. "
                + "Requires permission: Ins.Claim.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceClaim.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    internal static async Task<IResult> Upload(
        Guid claimId,
        string documentKind,
        IFormFile file,
        bool requestedByInsurer,
        IntegrationDbContext db,
        IClaimDocumentStore store,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = ClaimDocumentErrors.Empty });

        if (string.IsNullOrWhiteSpace(documentKind))
            return Results.BadRequest(new { error = "INSURANCE_CLAIM_DOCUMENT_KIND_REQUIRED" });

        // The tenant query filter scopes this, so a dossier of another tenant is simply absent —
        // 404 and never 403: the existence of a claim must not leak.
        var claim = await db.Claims
            .Where(c => c.Id == claimId)
            .Select(c => new { c.Id, c.Status })
            .FirstOrDefaultAsync(ct);

        if (claim is null)
            return Results.NotFound(new { error = ClaimErrors.ClaimNotFound });

        // A settled dossier takes no further pieces: the object would sit in the store with
        // nothing ever reading it, attached to a case the insurer has closed. Indemnified and
        // Refused are the two the aggregate calls terminal.
        if (claim.Status is ClaimStatus.Indemnified or ClaimStatus.Refused)
        {
            return Results.Conflict(new
            {
                error = "INSURANCE_CLAIM_SETTLED",
                status = claim.Status.ToString(),
            });
        }

        var logger = loggerFactory.CreateLogger(typeof(ClaimDocumentEndpoints));

        StoredClaimDocument stored;

        try
        {
            await using var stream = file.OpenReadStream();

            stored = await store.StoreAsync(
                currentUser.TenantId, claimId, stream, file.ContentType, ct);
        }
        catch (DomainException ex)
        {
            // The store refuses an unsupported content type, an oversized file and an empty one
            // with stable codes. The caller's problem, not a 500.
            return Results.BadRequest(new { error = ex.Message });
        }

        // Read AFTER storing and not before: the digest is of the plaintext the store computed,
        // and a check against a digest the caller supplied would be a check against a claim.
        // ux_ins_claim_document_digest is what makes this true under concurrency — EF InMemory
        // honours no unique index, so this read is the only half the suite can prove.
        var duplicate = await db.ClaimDocuments
            .AnyAsync(d => d.ClaimId == claimId && d.Sha256 == stored.Sha256, ct);

        if (duplicate)
        {
            // The object just written is removed: keeping it would leave an encrypted blob no row
            // points at, growing once per double-click.
            await Cleanup(store, currentUser.TenantId, stored.StorageRef, logger, claimId);

            return Results.Conflict(new { error = ClaimDocumentErrors.Duplicate });
        }

        var document = ClaimDocument.Create(
            tenantId: currentUser.TenantId,
            claimId: claimId,
            documentKind: documentKind,
            fileName: file.FileName,
            contentType: stored.ContentType,
            sizeBytes: stored.SizeBytes,
            sha256: stored.Sha256,
            storageRef: stored.StorageRef,
            uploadedBy: currentUser.Id,
            clock: clock,
            requestedByInsurer: requestedByInsurer);

        db.ClaimDocuments.Add(document);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The object is written BEFORE the row, and this endpoint is not a command — so it
            // gets no TransactionBehavior and a failed save would leave an encrypted object
            // nothing points at. The order is not invertible: a row pointing at an object that was
            // never stored would make the list advertise a piece that 404s, which is worse than an
            // orphan the retention sweep will never reach.
            logger.LogError(ex,
                "Could not register claim piece {StorageRef} on dossier {ClaimId}; removing the "
                + "stored object so it is not orphaned", stored.StorageRef, claimId);

            await Cleanup(store, currentUser.TenantId, stored.StorageRef, logger, claimId);

            throw;
        }

        // The FILE NAME is not logged — an agent names a scan after the customer often enough that
        // it is personal data, and logs travel further than the claims screen.
        logger.LogInformation(
            "Claim piece stored on dossier {ClaimId}: {DocumentId}, {Kind}, {Bytes} bytes, "
            + "{ContentType}, pending the antivirus pass",
            claimId, document.Id, documentKind, stored.SizeBytes, stored.ContentType);

        return Results.Created(
            $"/api/v1/integration/insurance/claims/{claimId}/documents/{document.Id}",
            new UploadClaimDocumentResponse(
                document.Id, documentKind, stored.ContentType, stored.SizeBytes, stored.Sha256,
                document.ScanStatus.ToString(), requestedByInsurer));
    }

    internal static async Task<IResult> List(
        Guid claimId, IntegrationDbContext db, CancellationToken ct)
    {
        var exists = await db.Claims.AnyAsync(c => c.Id == claimId, ct);

        if (!exists)
            return Results.NotFound(new { error = ClaimErrors.ClaimNotFound });

        var documents = await db.ClaimDocuments
            .Where(d => d.ClaimId == claimId)
            .OrderBy(d => d.UploadedAt)
            .ToListAsync(ct);

        return Results.Ok(documents.Select(ClaimDocumentDto.From).ToList());
    }

    internal static async Task<IResult> Download(
        Guid claimId,
        Guid documentId,
        IntegrationDbContext db,
        IClaimDocumentStore store,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        // Both ids in the predicate: a document id alone would let a piece be read through the
        // wrong dossier's route, and the pair is what the perimeter of a later agency check would
        // hang off.
        var document = await db.ClaimDocuments
            .Where(d => d.Id == documentId && d.ClaimId == claimId)
            .FirstOrDefaultAsync(ct);

        if (document is null)
            return Results.NotFound(new { error = ClaimDocumentErrors.NotFound });

        if (!document.IsTransmittable)
        {
            return Results.Conflict(new
            {
                error = ClaimDocumentErrors.NotScanned,
                scanStatus = document.ScanStatus.ToString(),
            });
        }

        var bytes = await store.OpenAsync(currentUser.TenantId, document.StorageRef, ct);

        // Null covers a purged object and a reference that was never this tenant's — and the two
        // must be indistinguishable, which is why the store answers null rather than throwing.
        if (bytes is null)
            return Results.NotFound(new { error = ClaimDocumentErrors.NotFound });

        return Results.File(bytes, document.ContentType, document.FileName);
    }

    private static async Task Cleanup(
        IClaimDocumentStore store, Guid tenantId, string storageRef, ILogger logger, Guid claimId)
    {
        try
        {
            // CancellationToken.None: the cleanup must run even when the request is being torn
            // down, which is one of the ways we got here.
            await store.DeleteAsync(tenantId, storageRef, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallowed so the ORIGINAL failure is what reaches the caller and the logs: a store
            // that cannot delete leaves one unreferenced object, which is a cleanup problem, while
            // hiding why the upload failed is a debugging problem.
            logger.LogError(ex,
                "Could not remove the orphaned claim object {StorageRef} of dossier {ClaimId}",
                storageRef, claimId);
        }
    }
}

/// <param name="ScanStatus">
/// Always <c>Pending</c> on a fresh upload. Returned so a screen can say "waiting on the antivirus"
/// rather than showing a piece as ready the moment it is accepted.
/// </param>
public sealed record UploadClaimDocumentResponse(
    Guid DocumentId,
    string DocumentKind,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string ScanStatus,
    bool RequestedByInsurer);

/// <param name="IsTransmittable">
/// The aggregate's own gate, so a screen does not re-derive it — and so "why has this not been
/// sent" has one answer.
/// </param>
public sealed record ClaimDocumentDto(
    Guid Id,
    Guid ClaimId,
    string DocumentKind,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string ScanStatus,
    DateTimeOffset? ScannedAt,
    string? ScanDetail,
    bool RequestedByInsurer,
    Guid UploadedBy,
    DateTimeOffset UploadedAt,
    DateTimeOffset? TransmittedAt,
    Guid? TransmitCommandId,
    bool IsTransmittable)
{
    internal static ClaimDocumentDto From(ClaimDocument d) => new(
        Id: d.Id,
        ClaimId: d.ClaimId,
        DocumentKind: d.DocumentKind,
        FileName: d.FileName,
        ContentType: d.ContentType,
        SizeBytes: d.SizeBytes,
        Sha256: d.Sha256,
        ScanStatus: d.ScanStatus.ToString(),
        ScannedAt: d.ScannedAt,
        ScanDetail: d.ScanDetail,
        RequestedByInsurer: d.RequestedByInsurer,
        UploadedBy: d.UploadedBy,
        UploadedAt: d.UploadedAt,
        TransmittedAt: d.TransmittedAt,
        TransmitCommandId: d.TransmitCommandId,
        IsTransmittable: d.IsTransmittable);
}
