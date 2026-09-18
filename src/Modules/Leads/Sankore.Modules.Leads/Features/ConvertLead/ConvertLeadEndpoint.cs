namespace Sankore.Modules.Leads.Features.ConvertLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ConvertLeadEndpoint
{
    public static IEndpointRouteBuilder MapConvertLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/convert", Handle)
            .WithName("ConvertLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanConvertLead.Code)
            .Produces<ConvertLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ConvertLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new ConvertLeadCommand(leadId, req.CustomerId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Conversion failed", detail: result.Error, statusCode: 422);
    }
}

/// <param name="CustomerId">
/// Optional. Supply a pre-existing customer id to link (e.g. when the
/// Customers module created the record first). Leave null for auto-generation.
/// </param>
public sealed record ConvertLeadRequest(Guid? CustomerId = null);
