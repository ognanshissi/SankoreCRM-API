namespace Sankore.Modules.Kyc.Features.Documents.UploadDocument;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Modules.Kyc.Infrastructure;
using Sankore.Modules.Kyc.Infrastructure.Storage;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Puts an identity-document or selfie image into the encrypted store and hands back the opaque
/// reference that <c>POST kyc-files/{id}/verify</c> consumes.
///
/// <para>
/// It is a thin endpoint and not a MediatR slice on purpose, and that is still true now that it
/// writes a row: only METADATA is persisted here — where the object is, how big it is, its digest,
/// who uploaded it. Giving it a command would put the image BYTES through the audit pipeline, which
/// serialises what it is given. The verification command remains what turns a reference into OCR
/// evidence.
/// </para>
///
/// <para>
/// The <c>kyc_documents</c> row is what makes a document a thing rather than a loose reference: it
/// is what lets a file's images be listed, what lets a validator accept or refuse one, and what
/// lets <c>GET documents/{storageRef}</c> confirm a ref belongs to this file before a verification
/// has ever run.
/// </para>
/// </summary>
internal static class UploadKycDocumentEndpoint
{
    internal static IEndpointRouteBuilder MapUploadKycDocument(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/documents", Handle)
            .WithName("UploadKycDocument")
            .WithSummary("Upload an identity document or selfie image")
            .WithDescription(
                "Stores the image encrypted and returns an opaque reference to hand to "
                + "POST kyc-files/{id}/verify. Accepts image/jpeg, image/png and application/pdf, "
                + "up to the tenant's configured size limit. Requires permission: kyc:manage.")
            .RequireAuthorization(Permissions.CanManageKycFile.Code)
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadKycDocumentResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader()
            .DisableAntiforgery();

        return app;
    }

    internal static async Task<IResult> Handle(
        Guid kycFileId,
        KycDocumentKind kind,
        IFormFile file,
        KycDbContext db,
        IKycDocumentStore store,
        ICurrentUser currentUser,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return Results.BadRequest(new { error = "KYC_DOCUMENT_EMPTY" });

        // The query filter scopes this to the caller's tenant, so a file of another tenant is
        // simply absent — 404, never 403: the existence of a customer must not leak across
        // tenants.
        var kycFile = await db.KycFiles
            .Where(f => f.Id == kycFileId)
            .Select(f => new { f.Id, f.Status })
            .FirstOrDefaultAsync(ct);

        if (kycFile is null)
            return Results.NotFound(new { error = KycErrors.FileNotFound });

        // Uploading to a file that is already decided would put an image in the store that no
        // verification will ever read, and leave evidence attached to a closed case.
        if (!CanReceiveDocuments(kycFile.Status))
            return Results.Conflict(new { error = KycErrors.InvalidTransition, status = kycFile.Status.ToString() });

        var logger = loggerFactory.CreateLogger(typeof(UploadKycDocumentEndpoint));

        try
        {
            await using var stream = file.OpenReadStream();

            var stored = await store.StoreAsync(
                currentUser.TenantId, kycFileId, kind, stream, file.ContentType, ct);

            var document = KycDocument.Register(
                tenantId: currentUser.TenantId,
                kycFileId: kycFileId,
                kind: kind,
                storageRef: stored.StorageRef,
                contentType: stored.ContentType,
                sizeBytes: stored.SizeBytes,
                sha256: stored.Sha256,
                uploadedBy: currentUser.Id,
                clock: clock);

            db.KycDocuments.Add(document);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // The object is written BEFORE the row, and this endpoint is not a command — so it
                // gets no TransactionBehavior and a failed save would leave an encrypted object
                // nothing points at. Delete it and report the failure.
                //
                // The order is not invertible: a row pointing at an object that was never stored
                // would make the list advertise an image that 404s, which is worse than an orphan.
                logger.LogError(ex,
                    "Could not register KYC document {StorageRef} for file {KycFileId}; "
                    + "removing the stored object so it is not orphaned", stored.StorageRef, kycFileId);

                try
                {
                    // CancellationToken.None: the cleanup must run even when the request is being
                    // torn down, which is one of the ways we got here.
                    await store.DeleteAsync(currentUser.TenantId, stored.StorageRef, CancellationToken.None);
                }
                catch (Exception cleanupFailure)
                {
                    // Swallowed so the ORIGINAL failure is what reaches the caller and the logs: a
                    // store that cannot delete leaves one unreferenced object, which is a cleanup
                    // problem, while hiding why the upload failed is a debugging problem.
                    logger.LogError(cleanupFailure,
                        "Could not remove the orphaned KYC object {StorageRef} of file {KycFileId}",
                        stored.StorageRef, kycFileId);
                }

                throw;
            }

            // The filename is NOT logged: an agent names a scan after the customer often enough
            // that it is personal data, and logs travel further than the KYC screen.
            logger.LogInformation(
                "KYC document stored for file {KycFileId}: {DocumentId}, {Kind}, {Bytes} bytes, {ContentType}",
                kycFileId, document.Id, kind, stored.SizeBytes, stored.ContentType);

            return Results.Created(
                $"/api/v1/kyc-files/{kycFileId}/documents/{stored.StorageRef}",
                new UploadKycDocumentResponse(
                    document.Id, stored.StorageRef, kind.ToString(),
                    stored.ContentType, stored.SizeBytes, stored.Sha256));
        }
        catch (DomainException ex)
        {
            // The store refuses an unsupported content type, an oversized file and an empty one
            // with stable KYC_DOCUMENT_* codes. They are the caller's problem, not a 500.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Collection, a file sent back for complement, and a verification in flight — the three
    /// states where another image can still change the outcome. Everything else is decided.
    /// </summary>
    internal static bool CanReceiveDocuments(KycFileStatus status) =>
        status is KycFileStatus.Collecting
               or KycFileStatus.ComplementRequired
               or KycFileStatus.Verifying;
}

/// <param name="DocumentId">
/// The registry row. This is what the review endpoint addresses — the storage ref addresses the
/// bytes, the id addresses the decision.
/// </param>
/// <param name="StorageRef">Opaque. Hand it to POST kyc-files/{id}/verify.</param>
/// <param name="Sha256">Of the plaintext, so a later check can prove the image was not swapped.</param>
internal sealed record UploadKycDocumentResponse(
    Guid DocumentId,
    string StorageRef,
    string Kind,
    string ContentType,
    long SizeBytes,
    string Sha256);
