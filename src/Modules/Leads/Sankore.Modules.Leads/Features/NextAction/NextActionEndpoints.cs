using Sankore.Shared.Infrastructure.Auth;

namespace Sankore.Modules.Leads.Features.NextAction;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.NextAction.AcknowledgeNextAction;
using Sankore.Modules.Leads.Features.NextAction.GetNextAction;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class NextActionEndpoints
{
    public static IEndpointRouteBuilder MapNextActionEndpoints(this IEndpointRouteBuilder app)
    {
        // GET leads/{leadId}/next-action
        app.MapGet("{leadId:guid}/next-action", GetNextAction)
            .WithName("GetLeadNextAction")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<NextActionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST leads/{leadId}/next-action/acknowledge
        app.MapPost("{leadId:guid}/next-action/acknowledge", AcknowledgeNextAction)
            .WithName("AcknowledgeLeadNextAction")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<AcknowledgeNextActionResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> GetNextAction(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetNextActionQuery(leadId), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Next action unavailable", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }

    private static async Task<IResult> AcknowledgeNextAction(
        Guid leadId,
        AcknowledgeNextActionRequest req,
        ISender sender,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        var acknowledgedBy = currentUser.Id;
        var result = await sender.Send(
            new AcknowledgeNextActionCommand(
                LeadId:          leadId,
                Action:          req.Action,
                RescheduledAt:   req.RescheduledAt), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Acknowledge failed", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }
}

public sealed record AcknowledgeNextActionRequest(
    NextActionAcknowledgement Action,
    /// <summary>Required when Action = Reschedule.</summary>
    DateTimeOffset? RescheduledAt = null);
