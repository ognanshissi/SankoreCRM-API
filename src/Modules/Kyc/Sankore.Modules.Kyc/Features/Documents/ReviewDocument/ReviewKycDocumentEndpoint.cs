namespace Sankore.Modules.Kyc.Features.Documents.ReviewDocument;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ReviewKycDocumentEndpoint
{
    internal static IEndpointRouteBuilder MapReviewKycDocument(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/documents/{documentId:guid}/review", Handle)
            .WithName("ReviewKycDocument")
            .WithSummary("Accept or refuse one uploaded KYC document")
            .WithDescription(
                "One validator's verdict on one image, with a mandatory motive when refused. "
                + "Accepting does NOT advance the file — the file-level decision stays a separate, "
                + "deliberate act — while refusing sends it back to ComplementRequired so the agent "
                + "produces a better image. A document can be decided once: a refused one is "
                + "replaced by a new upload, not edited. Allowed while the file is Verifying or "
                + "Validating. Requires permission: kyc:document:validate.")
            .RequireAuthorization(Permissions.CanValidateKycDocument.Code)
            .Produces<ReviewKycDocumentResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid kycFileId,
        Guid documentId,
        ReviewKycDocumentRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ReviewKycDocumentCommand(
            KycFileId: kycFileId,
            DocumentId: documentId,
            Decision: req.Decision,
            Reason: req.Reason), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            // 404 and never 403, for the same reason as everywhere else in this module.
            KycErrors.FileNotFound or KycErrors.DocumentNotFound
                => Results.NotFound(new { error = result.Error }),

            // 409 rather than 400: the request was well formed, it just arrived late. Somebody
            // decided this document, replaced it, or moved the file between the screen's read and
            // this call — the front recovers by reloading, not by asking the user to fix a field.
            KycErrors.DocumentAlreadyReviewed
                or KycErrors.DocumentNotCurrent
                or KycErrors.InvalidTransition
                or KycErrors.ConcurrencyConflict
                => Results.Conflict(new { error = result.Error }),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

internal sealed record ReviewKycDocumentRequest(
    KycDocumentReviewDecision Decision,
    string? Reason = null);
