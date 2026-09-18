namespace Sankore.Modules.Leads.Features.ReopenLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ReopenLeadEndpoint
{
    public static IEndpointRouteBuilder MapReopenLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/open", Handle)
            .WithName("ReopenLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanUpdateLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReopenLeadCommand(leadId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Open failed", detail: result.Error, statusCode: 422);
    }
}
