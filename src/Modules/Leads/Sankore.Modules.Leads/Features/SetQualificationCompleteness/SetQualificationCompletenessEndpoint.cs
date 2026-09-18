namespace Sankore.Modules.Leads.Features.SetQualificationCompleteness;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class SetQualificationCompletenessEndpoint
{
    public static IEndpointRouteBuilder MapSetQualificationCompleteness(
        this IEndpointRouteBuilder app)
    {
        app.MapPut("{leadId:guid}/qualification-completeness", Handle)
            .WithName("SetLeadQualificationCompleteness")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        SetQualificationCompletenessRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new SetQualificationCompletenessCommand(leadId, req.Completeness), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }
}

/// <param name="Completeness">Value between 0.0 (no data) and 1.0 (fully qualified).</param>
public sealed record SetQualificationCompletenessRequest(double Completeness);
