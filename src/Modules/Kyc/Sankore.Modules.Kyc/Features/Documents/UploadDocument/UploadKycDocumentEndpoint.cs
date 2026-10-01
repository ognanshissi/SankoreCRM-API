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
/// It is a thin endpoint and not a MediatR slice on purpose: nothing is persisted in the KYC
/// schema here. The image goes to the object store, the reference comes back, and the verification
/// command is what turns a reference into evidence. Giving it a command would put the image bytes
/// through the audit pipeline, which serialises what it is given.
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

            // The filename is NOT logged: an agent names a scan after the customer often enough
            // that it is personal data, and logs travel further than the KYC screen.
            logger.LogInformation(
                "KYC document stored for file {KycFileId}: {Kind}, {Bytes} bytes, {ContentType}",
                kycFileId, kind, stored.SizeBytes, stored.ContentType);

            return Results.Created(
                $"/api/v1/kyc-files/{kycFileId}/documents/{stored.StorageRef}",
                new UploadKycDocumentResponse(
                    stored.StorageRef, kind.ToString(), stored.ContentType, stored.SizeBytes, stored.Sha256));
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

/// <param name="StorageRef">Opaque. Hand it to POST kyc-files/{id}/verify.</param>
/// <param name="Sha256">Of the plaintext, so a later check can prove the image was not swapped.</param>
internal sealed record UploadKycDocumentResponse(
    string StorageRef,
    string Kind,
    string ContentType,
    long SizeBytes,
    string Sha256);
