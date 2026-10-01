namespace Sankore.Modules.Kyc.Features.Verification.GetVerification;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycVerificationEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycVerification(this IEndpointRouteBuilder app)
    {
        app.MapGet("{kycFileId:guid}/verification", Handle)
            .WithName("GetKycVerification")
            .WithSummary("Read the latest verification result of a KYC file")
            .WithDescription(
                "Score, level, per-criterion contributions as the biometry service named them, "
                + "service flags and the latest face comparison. Carries NO document number, no "
                + "OCR value and no machine-readable zone: those are the identity-document slice, "
                + "which is gated separately. Requires permission: kyc:read.")
            // kyc:read, not kyc:verify: reading where a file stands is not running a verification.
            // The two were conflated in the original request for this screen, which would have made
            // "may re-run a biometric check" the price of opening a panel.
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycVerificationDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid kycFileId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetKycVerificationQuery(kycFileId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            // 404 and never 403: the existence of another tenant's file must not leak.
            : Results.NotFound(new { error = result.Error });
    }
}
