namespace Sankore.Modules.Leads.Features.GetLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetLeadEndpoint
{
    public static IEndpointRouteBuilder MapGetLead(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}", Handle)
            .WithName("GetLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
