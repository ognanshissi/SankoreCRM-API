namespace Sankore.Modules.Leads.Features.NurtureLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class NurtureLeadEndpoint
{
    public static IEndpointRouteBuilder MapNurtureLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/nurture", Handle)
            .WithName("NurtureLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanNurtureLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new NurtureLeadCommand(leadId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Nurture failed", detail: result.Error, statusCode: 422);
    }
}
