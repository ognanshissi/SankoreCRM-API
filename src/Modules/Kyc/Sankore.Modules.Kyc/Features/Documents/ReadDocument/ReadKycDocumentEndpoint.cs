namespace Sankore.Modules.Kyc.Features.Documents.ReadDocument;

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
/// Streams a stored KYC image back to an authorised user, and records that it was opened.
///
/// <para>
/// An authenticated stream rather than an expiring URL, deliberately. A signed URL would let the
/// audit record only that a link was ISSUED — the link then travels by chat, is opened three days
/// later by somebody else, and the log says nothing about it. Here the read itself is the audited
/// event, and the bytes never leave a request carrying the reader's identity.
/// </para>
///
/// <para>
/// The permission is <c>kyc:document:reveal</c>, the same one that guards revealing a document
/// NUMBER — because the image contains the number. Granting the picture while withholding the
/// digits would be a distinction without a difference.
/// </para>
/// </summary>
internal static class ReadKycDocumentEndpoint
{
    internal static IEndpointRouteBuilder MapReadKycDocument(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}/documents/{storageRef}", Handle)
            .WithName("ReadKycDocument")
            .WithSummary("Open a stored KYC image")
            .WithDescription(
                "Streams the decrypted image and records WHO opened it in kyc_document_access_logs. "
                + "Pass ?reason= when the caller stated one. Requires permission: kyc:document:reveal.")
            .RequireAuthorization(Permissions.CanRevealKycDocumentNumber.Code)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream")
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader()
            // Same bucket as the other sensitive reads: a script walking storage refs is the
            // threat this endpoint has, and it is cheap to make it slow.
            .RequireRateLimiting("auth");

        return app;
    }

    internal static async Task<IResult> Handle(
        Guid kycFileId,
        string storageRef,
        string? reason,
        KycDbContext db,
        IKycDocumentStore store,
        ICurrentUser currentUser,
        TimeProvider clock,
        IHttpContextAccessor httpContextAccessor,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        // The query filter scopes this to the caller's tenant: a file of another tenant is absent,
        // so the answer is 404 and never 403 — the existence of a customer must not leak.
        var exists = await db.KycFiles.AnyAsync(f => f.Id == kycFileId, ct);
        if (!exists)
            return Results.NotFound(new { error = KycErrors.FileNotFound });

        // The ref must belong to THIS file. Without the check, a caller holding any valid ref
        // could read it through any file id they are allowed to see — the file id would be
        // decoration and the audit row would name the wrong file.
        var belongs = await db.KycIdentityDocuments
            .AnyAsync(d => d.KycFileId == kycFileId && d.StorageRef == storageRef, ct)
            || await db.KycFaceVerifications
                .AnyAsync(v => v.KycFileId == kycFileId && v.SelfieStorageRef == storageRef, ct);

        if (!belongs)
            return Results.NotFound(new { error = KycErrors.DocumentNotFound });

        var stream = await store.OpenAsync(currentUser.TenantId, storageRef, ct);
        if (stream is null)
            return Results.NotFound(new { error = KycErrors.DocumentNotFound });

        // Written BEFORE the bytes are handed over, and committed before the response starts: a
        // stream that fails halfway must still leave the trace that someone asked for it. The
        // opposite order loses exactly the reads an investigation cares about.
        db.KycDocumentAccessLogs.Add(KycDocumentAccessLog.Record(
            tenantId: currentUser.TenantId,
            kycFileId: kycFileId,
            storageRef: storageRef,
            actorUserId: currentUser.Id,
            clock: clock,
            correlationId: httpContextAccessor.HttpContext?.TraceIdentifier,
            reason: reason));

        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger(typeof(ReadKycDocumentEndpoint)).LogInformation(
            "KYC image of file {KycFileId} opened by {ActorId}", kycFileId, currentUser.Id);

        // No file name: a download named after the customer is the kind of thing that ends up in
        // a shared folder. The content type is deliberately generic — the store does not keep the
        // original one on the object, and guessing it from the ref would be worse than letting the
        // browser sniff.
        return Results.Stream(stream, "application/octet-stream");
    }
}
