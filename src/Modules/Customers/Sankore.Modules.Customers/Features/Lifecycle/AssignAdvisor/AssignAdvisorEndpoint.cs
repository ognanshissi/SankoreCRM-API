namespace Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class AssignAdvisorEndpoint
{
    public static IEndpointRouteBuilder MapAssignAdvisor(this IEndpointRouteBuilder app)
    {
        app.MapPut("{clientId:guid}/advisor", Handle)
            .WithName("AssignClientAdvisor")
            .WithSummary("Assign or clear the client's advisor")
            .WithDescription(
                "Sets the account officer owning the relationship. Send advisorUserId = null to clear it. " +
                "The advisor must be an active user of the client's own agency, otherwise " +
                "ADVISOR_NOT_ELIGIBLE (400). Returns 404 CLIENT_NOT_FOUND when the client is unknown or " +
                "outside the caller's agency perimeter, and CLIENT_READ_ONLY (409) on an archived or " +
                "merged record. Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
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
        AssignAdvisorRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new AssignAdvisorCommand(clientId, req.AdvisorUserId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : LifecycleHttp.ToProblem(result.Error!);
    }
}

public sealed record AssignAdvisorRequest(Guid? AdvisorUserId);
