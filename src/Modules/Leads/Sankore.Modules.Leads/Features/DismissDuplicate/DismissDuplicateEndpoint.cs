namespace Sankore.Modules.Leads.Features.DismissDuplicate;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DismissDuplicateEndpoint
{
    public static IEndpointRouteBuilder MapDismissDuplicate(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/dismiss-duplicate", Handle)
            .WithName("DismissDuplicate")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanDismissDuplicate.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        DismissDuplicateRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId    = http.User.GetTenantId();
        var dismissedBy = http.User.GetUserId();

        var result = await sender.Send(new DismissDuplicateCommand(
            TenantId:        tenantId,
            LeadId:          leadId,
            CandidateLeadId: req.CandidateLeadId,
            DismissedBy:     dismissedBy,
            Reason:          req.Reason), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "LEAD_NOT_FOUND" or "CANDIDATE_LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Dismiss duplicate failed", detail: result.Error, statusCode: 422);
        }

        return Results.NoContent();
    }
}

/// <param name="CandidateLeadId">The lead identified as a potential duplicate that the agent has reviewed and determined is NOT a duplicate.</param>
/// <param name="Reason">Optional free-text justification (e.g. "Different person, same name").</param>
public sealed record DismissDuplicateRequest(
    Guid CandidateLeadId,
    string? Reason = null);
