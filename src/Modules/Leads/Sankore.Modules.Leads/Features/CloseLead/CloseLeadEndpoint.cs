namespace Sankore.Modules.Leads.Features.CloseLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

public static class CloseLeadEndpoint
{
    public static IEndpointRouteBuilder MapCloseLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/close", Handle)
            .WithName("CloseLead")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanCloseLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        CloseLeadRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new CloseLeadCommand(leadId, req.Reason, req.Detail, req.ExpectedUpdatedAt), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error switch
            {
                "LEAD_NOT_FOUND" => Results.NotFound(),
                "CONFLICT"       => Results.Conflict(new { error = result.Error }),
                _ => Results.Problem(title: "Close failed", detail: result.Error, statusCode: 422)
            };
    }
}

public sealed record CloseLeadRequest(
    LeadCloseReason Reason,
    string? Detail = null,
    /// <summary>UpdatedAt read with the lead; echo it back to detect concurrent edits.</summary>
    DateTimeOffset? ExpectedUpdatedAt = null);
