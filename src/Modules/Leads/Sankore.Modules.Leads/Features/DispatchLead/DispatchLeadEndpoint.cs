namespace Sankore.Modules.Leads.Features.DispatchLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Extensions;

/// <summary>
/// HTTP facade for this slice. Deliberately trivial: it reads the tenant
/// from the JWT, builds the command, sends it through MediatR, and maps
/// the Result to an HTTP response. All business logic lives in the
/// handler — this file should never grow beyond a few lines.
/// </summary>
public static class DispatchLeadEndpoint
{
    public static IEndpointRouteBuilder MapDispatchLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/leads/{leadId:guid}/dispatch", Handle)
            .WithName("DispatchLead")
            .WithTags("Leads")
            .RequireAuthorization("Leads.Dispatch")
            .Produces<DispatchLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        DispatchLeadRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        var result = await sender.Send(
            new DispatchLeadCommand(leadId, tenantId, req.Strategy), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(
                title: "Dispatching failed",
                detail: result.Error,
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }
}

public sealed record DispatchLeadRequest(
    DispatchingStrategy Strategy = DispatchingStrategy.CompatibilityScoring);
