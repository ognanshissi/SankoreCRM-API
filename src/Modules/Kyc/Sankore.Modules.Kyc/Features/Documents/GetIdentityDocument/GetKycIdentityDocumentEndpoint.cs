namespace Sankore.Modules.Kyc.Features.Documents.GetIdentityDocument;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycIdentityDocumentEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycIdentityDocument(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}/identity-document", Handle)
            .WithName("GetKycIdentityDocument")
            .WithSummary("Read the OCR fields and machine-readable zone of a KYC file's document")
            .WithDescription(
                "The fields the service read, its confidence per field, and the parsed "
                + "machine-readable zone. The document number comes back MASKED (first two and last "
                + "two characters): an agent must recognise which document they are correcting "
                + "without this becoming a way to read the number. The number in clear is "
                + "kyc:document:reveal and is audited; the raw MRZ line is never stored at all. "
                + "Requires permission: kyc:read.")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycIdentityDocumentDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid kycFileId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetKycIdentityDocumentQuery(kycFileId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
