namespace Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class ArchiveClientEndpoint
{
    public static IEndpointRouteBuilder MapArchiveClient(this IEndpointRouteBuilder app)
    {
        app.MapPost("{clientId:guid}/archive", Handle)
            .WithName("ArchiveClient")
            .WithSummary("Archive a client")
            .WithDescription(
                "Soft end of life: the client becomes read-only and ArchivedAt is stamped. The row is " +
                "never deleted — this module exposes no physical-delete endpoint at all. A motive is " +
                "mandatory (REASON_REQUIRED). Returns CLIENT_HAS_ACTIVE_COMMITMENTS (409) when a module " +
                "holding financial commitments still reports an open engagement, " +
                "INVALID_STATUS_TRANSITION from a status that cannot be archived, and 404 " +
                "CLIENT_NOT_FOUND when unknown or outside the caller's agency perimeter. " +
                "Requires permission: customers:archive.")
            .RequireAuthorization(Permissions.CanArchiveCustomer.Code)
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
        ArchiveClientRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ArchiveClientCommand(clientId, req.Reason), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}

public sealed record ArchiveClientRequest(string Reason);
