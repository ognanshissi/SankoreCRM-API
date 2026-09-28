namespace Sankore.Modules.Customers.Features.Lifecycle.TransferClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class TransferClientEndpoint
{
    public static IEndpointRouteBuilder MapTransferClient(this IEndpointRouteBuilder app)
    {
        app.MapPut("{clientId:guid}/agency", Handle)
            .WithName("TransferClientToAgency")
            .WithSummary("Transfer a client to another agency")
            .WithDescription(
                "Moves the client's portfolio to another agency and publishes ClientTransferredEvent. " +
                "The advisor is kept only when they also belong to the destination agency, otherwise the " +
                "assignment is reset and the response carries advisorUserId = null. A motive is " +
                "mandatory (REASON_REQUIRED). Returns AGENCY_OUT_OF_SCOPE (403) when the destination — or " +
                "the source — is outside the caller's agency perimeter or unknown, and 404 " +
                "CLIENT_NOT_FOUND when the client is unknown or invisible to the caller. Re-posting the " +
                "current agency is a no-op. Requires permission: customers:update_sensitive.")
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
        TransferClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new TransferClientCommand(clientId, req.TargetAgencyId, req.Reason), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}

public sealed record TransferClientRequest(Guid TargetAgencyId, string Reason);
