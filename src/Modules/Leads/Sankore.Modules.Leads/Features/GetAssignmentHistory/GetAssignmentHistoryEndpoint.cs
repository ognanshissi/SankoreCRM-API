namespace Sankore.Modules.Leads.Features.GetAssignmentHistory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetAssignmentHistoryEndpoint
{
    public static IEndpointRouteBuilder MapGetAssignmentHistory(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/assignments", Handle)
            .WithName("GetLeadAssignmentHistory")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<AssignmentDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetAssignmentHistoryQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
