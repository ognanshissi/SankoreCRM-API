namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpdateLeadOwnerEndpoint
{
    public static IEndpointRouteBuilder MapUpdateLeadOwner(this IEndpointRouteBuilder app)
    {
        app.MapPut("{leadId:guid}/owner", Handle)
            .WithName("UpdateLeadOwner")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanAssignLead.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        UpdateLeadOwnerRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var assignedBy = http.User.GetUserId();

        var result = await sender.Send(new UpdateLeadOwnerCommand(
            LeadId:           leadId,
            OwnerId:          req.OwnerId,
            AssignedBy:       assignedBy,
            AssignmentMethod: req.AssignmentMethod ?? "Manual",
            Reason:           req.Reason), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Owner update failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record UpdateLeadOwnerRequest(
    Guid OwnerId,
    string? Reason = null,
    /// <summary>Manual | Import | System. Defaults to Manual.</summary>
    string? AssignmentMethod = null);
