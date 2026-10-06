namespace Sankore.Modules.Kyc.Features.Verification.ManualValidation;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ManuallyValidateKycFileEndpoint
{
    internal static IEndpointRouteBuilder MapManuallyValidateKycFile(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/manual-validation", Handle)
            .WithName("ManuallyValidateKycFile")
            .WithSummary("Validate a KYC file's evidence by hand, with no biometric score")
            .WithDescription(
                "For a file the machine cannot resolve: the biometric service is unreachable, or it "
                + "keeps refusing a usable but worn document. Moves the file from Verifying or "
                + "ComplementRequired into the approval circuit on the validator's word, with a "
                + "mandatory motive. It is NOT an approval: the circuit still decides, it gains the "
                + "branch manager even on a low-risk file, and the validator can sign none of its "
                + "levels. Requires permission: kyc:document:validate.")
            .RequireAuthorization(Permissions.CanValidateKycDocument.Code)
            .Produces<ManuallyValidateKycFileResult>(StatusCodes.Status200OK)
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
        ManuallyValidateKycFileRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ManuallyValidateKycFileCommand(kycFileId, req.Reason), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            KycErrors.FileNotFound => Results.NotFound(new { error = result.Error }),

            // The file is not in a state this can move, or somebody moved it first.
            KycErrors.InvalidTransition or KycErrors.ConcurrencyConflict
                => Results.Conflict(new { error = result.Error }),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

internal sealed record ManuallyValidateKycFileRequest(string Reason);
