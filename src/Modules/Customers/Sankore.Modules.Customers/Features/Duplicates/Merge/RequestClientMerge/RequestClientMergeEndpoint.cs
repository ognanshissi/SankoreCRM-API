namespace Sankore.Modules.Customers.Features.Duplicates.Merge.RequestClientMerge;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class RequestClientMergeEndpoint
{
    public static IEndpointRouteBuilder MapRequestClientMerge(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("RequestClientMerge")
            .WithSummary("Request a client merge")
            .WithDescription(
                "Opens a four-eyes merge request. Nothing is merged yet: the request waits for a " +
                "different user to approve it. Both clients must be inside the caller's agency " +
                "perimeter. Requires permission: customers:merge.")
            .RequireAuthorization(Permissions.CanMergeCustomers.Code)
            .Produces<RequestClientMergeResult>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        RequestClientMergeRequest request,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new RequestClientMergeCommand(
            SurvivorClientId: request.SurvivorClientId,
            AbsorbedClientId: request.AbsorbedClientId,
            FieldChoices: request.FieldChoices ?? new Dictionary<string, string>(),
            Reason: request.Reason), ct);

        if (result.IsSuccess)
            return Results.Created($"/api/v1/clients/merges/{result.Value.MergeRequestId}", result.Value);

        return result.Error is CustomerErrors.ClientNotFound
            ? Results.NotFound(new { error = result.Error })
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

public sealed record RequestClientMergeRequest(
    Guid SurvivorClientId,
    Guid AbsorbedClientId,
    Dictionary<string, string>? FieldChoices,
    string Reason);
