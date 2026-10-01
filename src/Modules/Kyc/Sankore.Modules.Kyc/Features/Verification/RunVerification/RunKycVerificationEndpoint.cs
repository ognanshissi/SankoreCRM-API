namespace Sankore.Modules.Kyc.Features.Verification.RunVerification;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class RunKycVerificationEndpoint
{
    internal static IEndpointRouteBuilder MapRunKycVerification(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/verify", Handle)
            .WithName("RunKycVerification")
            .WithSummary("Run the biometric verification of a KYC file")
            .WithDescription(
                "Reads the identity document, compares the faces and scores the file. The images "
                + "must already be in the KYC document store; this endpoint takes their "
                + "references. 200 when the file was scored or the capture was refused, 202 when "
                + "the biometric service could not be reached and the attempt was queued.")
            .RequireAuthorization(Permissions.CanRunKycVerification.Code)
            .Produces<RunKycVerificationResponse>(StatusCodes.Status200OK)
            .Produces<RunKycVerificationResponse>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid kycFileId,
        RunKycVerificationRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var result = await sender.Send(new RunKycVerificationCommand(
            TenantId: currentUser.TenantId,
            KycFileId: kycFileId,
            DocumentStorageRef: req.DocumentStorageRef,
            SelfieStorageRef: req.SelfieStorageRef,
            RequestedBy: currentUser.Id), ct);

        if (result.IsFailure)
        {
            // 404 and never 403 for an unknown file, like GetKycFile: a file belonging to another
            // tenant is simply absent, and "forbidden" would confirm that it exists there.
            return result.Error == KycErrors.FileNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.BadRequest(new { error = result.Error });
        }

        var value = result.Value;

        var response = new RunKycVerificationResponse(
            KycFileId: value.KycFileId,
            Status: value.Status,
            Outcome: value.Outcome,
            ConfidenceScore: value.ConfidenceScore,
            ConfidenceLevel: value.ConfidenceLevel,
            Code: value.Code);

        // 202 rather than 200 when the service was unreachable: nothing was decided about this
        // customer, the file is still being verified, and a client that polls must be able to tell
        // that apart from a verdict without parsing an enum it may not know.
        return value.Outcome == RunKycVerificationOutcome.ServiceUnavailable
            ? Results.Accepted($"/api/v1/kyc-files/{value.KycFileId}", response)
            : Results.Ok(response);
    }
}

/// <param name="DocumentStorageRef">
/// Reference returned when the identity-document image was uploaded to the KYC document store.
/// Uploading belongs to its own slice; this endpoint never receives bytes.
/// </param>
internal sealed record RunKycVerificationRequest(
    string DocumentStorageRef,
    string SelfieStorageRef);

/// <summary>
/// Deliberately thin. The document number, the OCR fields and the MRZ never appear here: revealing
/// a number is its own slice behind its own permission (<c>kyc:document:reveal</c>), and an
/// endpoint that returned it as a side effect of verifying would make that permission decorative.
/// </summary>
internal sealed record RunKycVerificationResponse(
    Guid KycFileId,
    KycFileStatus Status,
    RunKycVerificationOutcome Outcome,
    int? ConfidenceScore,
    KycConfidenceLevel? ConfidenceLevel,
    string? Code);
