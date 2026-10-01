namespace Sankore.Modules.Kyc.Features.Approval.Decide;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class DecideKycApprovalEndpoint
{
    internal static IEndpointRouteBuilder MapDecideKycApproval(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/approval/decisions", Handle)
            .WithName("DecideKycApproval")
            .WithSummary("Sign one level of the KYC approval circuit")
            .WithDescription(
                "Only the next pending level can be decided. A refusal or a complement request "
                + "closes the circuit from any level; an approval validates the file only on the "
                + "last one. The approver is the authenticated caller — the agent who submitted "
                + "the file can never approve or refuse it.")
            .RequireAuthorization(Permissions.CanApproveKycFile.Code)
            .Produces<DecideKycApprovalResponse>(StatusCodes.Status200OK)
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
        DecideKycApprovalRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DecideKycApprovalCommand(
            KycFileId: kycFileId,
            Level: req.Level,
            Decision: req.Decision,
            Comment: req.Comment), ct);

        if (result.IsSuccess)
            return Results.Ok(new DecideKycApprovalResponse(
                result.Value.KycFileId,
                result.Value.Level,
                result.Value.Decision,
                result.Value.FileStatus,
                result.Value.Tier,
                result.Value.CircuitCompleted));

        return result.Error switch
        {
            // 404 and never 403: a file of another tenant is simply absent, and "forbidden" would
            // confirm that a customer exists there.
            KycErrors.FileNotFound => Results.NotFound(new { error = result.Error }),

            // 409 rather than 400 for the two races. Somebody else moved the circuit between the
            // screen's read and this call: the request was well formed, it just arrived late, and
            // the front retries by reloading rather than by asking the user to fix a field.
            KycErrors.ApprovalOutOfOrder or KycErrors.ApprovalStepAlreadyDecided
                => Results.Conflict(new { error = result.Error }),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <summary>
/// The level is sent explicitly so a stale screen is refused instead of signing whatever rung the
/// file has reached since it was loaded.
/// </summary>
internal sealed record DecideKycApprovalRequest(
    KycApprovalLevel Level,
    KycApprovalDecision Decision,
    string? Comment = null);

internal sealed record DecideKycApprovalResponse(
    Guid KycFileId,
    string Level,
    string Decision,
    string FileStatus,
    string Tier,
    bool CircuitCompleted);
