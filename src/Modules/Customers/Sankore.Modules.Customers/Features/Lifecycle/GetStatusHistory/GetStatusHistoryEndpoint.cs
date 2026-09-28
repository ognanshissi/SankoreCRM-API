namespace Sankore.Modules.Customers.Features.Lifecycle.GetStatusHistory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetStatusHistoryEndpoint
{
    public static IEndpointRouteBuilder MapGetStatusHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("{clientId:guid}/status-history", Handle)
            .WithName("GetClientStatusHistory")
            .WithSummary("List a client's status transitions")
            .WithDescription(
                "Paginated status history, most recent first: previous status, new status, motive, actor " +
                "and timestamp. Transitions applied by an incoming KYC event carry the SYSTEM actor " +
                "(all-zero GUID). Returns 404 CLIENT_NOT_FOUND when the client is unknown or outside the " +
                "caller's agency perimeter. Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<ClientStatusHistoryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId,
        ISender sender,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(new GetStatusHistoryQuery(clientId, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}
