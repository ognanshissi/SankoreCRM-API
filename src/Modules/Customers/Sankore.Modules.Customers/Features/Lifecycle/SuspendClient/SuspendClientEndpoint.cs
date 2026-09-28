namespace Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class SuspendClientEndpoint
{
    public static IEndpointRouteBuilder MapSuspendClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/suspend", Handle)
            .WithName("SuspendClient")
            .WithSummary("Suspend a client")
            .WithDescription(
                "Moves the client to Suspended. A motive is mandatory and is stored in the status " +
                "history and carried by ClientSuspendedEvent. Returns REASON_REQUIRED without a motive, " +
                "INVALID_STATUS_TRANSITION from a status that cannot be suspended, CLIENT_READ_ONLY on an " +
                "archived or merged record, and 404 CLIENT_NOT_FOUND when the client is unknown or outside " +
                "the caller's agency perimeter (the perimeter never reveals that a record exists). " +
                "Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<ClientLifecycleStateDto>(StatusCodes.Status200OK)
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
        Guid clientId,
        SuspendClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new SuspendClientCommand(clientId, req.Reason), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}

public sealed record SuspendClientRequest(string Reason);
