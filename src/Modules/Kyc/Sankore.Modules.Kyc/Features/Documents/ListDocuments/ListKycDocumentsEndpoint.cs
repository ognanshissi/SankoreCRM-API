namespace Sankore.Modules.Kyc.Features.Documents.ListDocuments;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListKycDocumentsEndpoint
{
    internal static IEndpointRouteBuilder MapListKycDocuments(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}/documents", Handle)
            .WithName("ListKycDocuments")
            .WithSummary("List the images attached to a KYC file and their review decisions")
            .WithDescription(
                "Metadata only, never bytes: fetching an image is "
                + "GET kyc-files/{id}/documents/{storageRef} under kyc:document:reveal, which is "
                + "audited per access. Images collected before per-document review come back with "
                + "a null id and decision NotReviewed — such a file was decided as a whole by the "
                + "approval circuit, and no per-document verdict exists to show. Requires "
                + "permission: kyc:read.")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycDocumentListDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid kycFileId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListKycDocumentsQuery(kycFileId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
