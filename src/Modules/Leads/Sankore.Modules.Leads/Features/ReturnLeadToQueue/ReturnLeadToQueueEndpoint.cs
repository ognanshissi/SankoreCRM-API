namespace Sankore.Modules.Leads.Features.ReturnLeadToQueue;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ReturnLeadToQueueEndpoint
{
    public static IEndpointRouteBuilder MapReturnLeadToQueue(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/return-to-queue", Handle)
            .WithName("ReturnLeadToQueue")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanAssignLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ReturnLeadToQueueCommand(leadId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Return to queue failed", detail: result.Error, statusCode: 422);
    }
}
