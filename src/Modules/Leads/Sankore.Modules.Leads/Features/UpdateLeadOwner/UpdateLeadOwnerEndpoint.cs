namespace Sankore.Modules.Leads.Features.UpdateLeadOwner;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        UpdateLeadOwnerRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateLeadOwnerCommand(leadId, req.OwnerId), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "LEAD_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Owner update failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record UpdateLeadOwnerRequest(Guid OwnerId);
