namespace Sankore.Modules.Customers.Features.Duplicates.Merge.GetClientMerge;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetClientMergeEndpoint
{
    public static IEndpointRouteBuilder MapGetClientMerge(this IEndpointRouteBuilder app)
    {
        app.MapGet("{mergeRequestId:guid}", Handle)
            .WithName("GetClientMerge")
            .WithSummary("Get a client merge request")
            .WithDescription(
                "Detail of one merge request: both clients, the per-field choices, the workflow " +
                "instance when M12 opened one, and the decision trail. " +
                "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<ClientMergeRequestDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid mergeRequestId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetClientMergeQuery(mergeRequestId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
