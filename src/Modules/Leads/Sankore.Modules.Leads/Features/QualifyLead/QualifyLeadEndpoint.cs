namespace Sankore.Modules.Leads.Features.QualifyLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class QualifyLeadEndpoint
{
    public static IEndpointRouteBuilder MapQualifyLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/qualify", Handle)
            .WithName("QualifyLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces<QualifyLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        QualifyLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new QualifyLeadCommand(leadId, req.Score, req.TriggerEvent ?? "MANUAL_QUALIFICATION"), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Qualification failed", detail: result.Error, statusCode: 422);
    }
}

/// <summary>
/// If Score is omitted the system auto-calculates it from lead attributes.
/// </summary>
public sealed record QualifyLeadRequest(int? Score = null, string? TriggerEvent = null);
