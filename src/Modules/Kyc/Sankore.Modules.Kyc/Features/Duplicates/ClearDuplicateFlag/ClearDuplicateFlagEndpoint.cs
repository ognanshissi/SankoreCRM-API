namespace Sankore.Modules.Kyc.Features.Duplicates.ClearDuplicateFlag;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ClearDuplicateFlagEndpoint
{
    internal static IEndpointRouteBuilder MapClearDuplicateFlag(this IEndpointRouteBuilder app)
    {
        app.MapPost("{kycFileId:guid}/duplicate-flag/clear", Handle)
            .WithName("ClearKycDuplicateFlag")
            .WithSummary("Lift a suspected duplicate document flag")
            .WithDescription(
                "Requires an explicit reason, recorded in the audit trail. The file's vigilance "
                + "level is NOT lowered: the file was once suspect and that stays true.")
            // Its own permission, not kyc:manage. The agent who collects a file must not be able
            // to dismiss the suspicion raised against it.
            .RequireAuthorization(Permissions.CanClearKycDuplicateFlag.Code)
            .Produces(StatusCodes.Status204NoContent)
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
        ClearDuplicateFlagRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var result = await sender.Send(new ClearDuplicateFlagCommand(
            KycFileId: kycFileId,
            Reason: req.Reason,
            ClearedBy: currentUser.Id), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        // 404 and never 403: a file of another tenant is simply absent, and "forbidden" would
        // confirm that a customer exists there.
        return result.Error == KycErrors.FileNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}

internal sealed record ClearDuplicateFlagRequest(string Reason);
