namespace Sankore.Modules.Kyc.Features.Reviews.RaiseReview;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class RaiseKycReviewEndpoint
{
    internal static IEndpointRouteBuilder MapRaiseKycReview(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/reviews", Handle)
            .WithName("RaiseKycReview")
            .WithSummary("Raise a KYC review without waiting for the periodic deadline")
            .WithDescription(
                "Schedules an event-driven review due today — a transaction alert, a new "
                + "document, a change of beneficial owner. An explicit reason is required and is "
                + "shown to the officer who picks the review up, so it must carry no sensitive "
                + "value. The daily sweep is what then notifies the agency.")
            .RequireAuthorization(Permissions.CanManageKycFile.Code)
            .Produces<RaiseKycReviewResult>(StatusCodes.Status200OK)
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
        RaiseKycReviewRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var result = await sender.Send(new RaiseKycReviewCommand(
            KycFileId: kycFileId,
            Reason: req.Reason,
            RaisedBy: currentUser.Id), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        // 404 and never 403: a file of another tenant is simply absent, and "forbidden" would
        // confirm that a customer exists there.
        return result.Error == KycErrors.FileNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}

internal sealed record RaiseKycReviewRequest(string Reason);
