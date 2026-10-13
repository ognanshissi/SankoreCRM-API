namespace Sankore.Modules.Kyc.Features.Routing.Controls.RecordControl;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class RecordKycAutoApprovalControlEndpoint
{
    internal static IEndpointRouteBuilder MapRecordKycAutoApprovalControl(
        this IEndpointRouteBuilder app)
    {
        app.MapPost("auto-approval-controls/{controlId:guid}/finding", Handle)
            .WithName("RecordKycAutoApprovalControl")
            .WithSummary("Record the compliance finding on a sampled automatic approval")
            .WithDescription(
                "Once per control: a recorded finding is evidence that a control was performed, "
                + "and re-recording it would rewrite who found what. A non-compliant verdict "
                + "requires a finding and opens an event-driven review on the file — the response "
                + "carries its id, or null when the verdict stands but the review could not be "
                + "scheduled. The file's responsible agent cannot control its own automatic "
                + "approval. Requires kyc:auto-approval:control.")
            .RequireAuthorization(Permissions.CanControlKycAutoApproval.Code)
            .Produces<RecordKycAutoApprovalControlResult>(StatusCodes.Status200OK)
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
        Guid controlId,
        RecordKycAutoApprovalControlRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RecordKycAutoApprovalControlCommand(controlId, req.Compliant, req.Finding), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            // 404 and never 403: a control of another tenant is absent, and "forbidden" would
            // confirm that an automatic approval happened there.
            KycErrors.AutoApprovalControlNotFound => Results.NotFound(new { error = result.Error }),

            // 409: somebody else recorded it between this screen's read and the click. The request
            // was well formed, it arrived late, and the front recovers by reloading.
            KycErrors.AutoApprovalControlAlreadyRecorded
                => Results.Conflict(new { error = result.Error }),

            // 403 here, unlike everywhere else in this module, and deliberately: the caller can
            // already SEE the row, so there is nothing left to hide — and "not found" would send a
            // compliance officer looking for a row that is in front of them.
            KycErrors.SelfApprovalForbidden
                => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <param name="Compliant">
/// True when the machine was right to approve without a human. False opens a review on the file.
/// </param>
/// <param name="Finding">
/// Mandatory when <paramref name="Compliant"/> is false. Never a document number, a date of birth
/// or an address: it is stored in clear and read by operators.
/// </param>
internal sealed record RecordKycAutoApprovalControlRequest(bool Compliant, string? Finding = null);
