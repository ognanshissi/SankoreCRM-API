namespace Sankore.Modules.Leads.Features.RecycleLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class RecycleLeadEndpoint
{
    public static IEndpointRouteBuilder MapRecycleLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/recycle", Handle)
            .WithName("RecycleLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanRecycleLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        RecycleLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new RecycleLeadCommand(leadId, req.NewSource, req.NewCampaign, req.Reason, req.ReactivateOn), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Recycle failed", detail: result.Error, statusCode: 422);
    }
}

/// <param name="NewSource">Optional new acquisition source to stamp on the recycled lead.</param>
/// <param name="NewCampaign">Optional new campaign to associate with the recycled lead.</param>
/// <param name="Reason">Why it is set aside: written on the resumption task.</param>
/// <param name="ReactivateOn">The day it comes back, after today. Omitted: the job's default delay, and no task.</param>
public sealed record RecycleLeadRequest(
    LeadSource? NewSource = null,
    string? NewCampaign = null,
    string? Reason = null,
    DateOnly? ReactivateOn = null);
