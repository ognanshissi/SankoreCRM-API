namespace Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ReactivateClientEndpoint
{
    public static IEndpointRouteBuilder MapReactivateClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/reactivate", Handle)
            .WithName("ReactivateClient")
            .WithSummary("Lift a client suspension")
            .WithDescription(
                "Lifts a suspension. The resulting status is Active when the KYC file is Approved and " +
                "PendingKyc otherwise — read it back from the response rather than assuming Active. " +
                "Returns INVALID_STATUS_TRANSITION when the client is not suspended, CLIENT_READ_ONLY on " +
                "an archived or merged record, and 404 CLIENT_NOT_FOUND when unknown or outside the " +
                "caller's agency perimeter. Requires permission: customers:update_sensitive.")
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

    private static async Task<IResult> Handle(Guid clientId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReactivateClientCommand(clientId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}
