namespace Sankore.Modules.Leads.Features.SetIntentLevel;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class SetIntentLevelEndpoint
{
    public static IEndpointRouteBuilder MapSetIntentLevel(this IEndpointRouteBuilder app)
    {
        app.MapPut("{leadId:guid}/intent-level", Handle)
            .WithName("SetLeadIntentLevel")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanQualifyLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        SetIntentLevelRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new SetIntentLevelCommand(leadId, req.IntentLevel), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Intent level update failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record SetIntentLevelRequest(LeadIntentLevel IntentLevel);
