namespace Sankore.Modules.Leads.Features.ListActivities;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ListActivitiesEndpoint
{
    public static IEndpointRouteBuilder MapListActivities(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/activities", Handle)
            .WithName("ListLeadActivities")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<ActivityDto>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        var result = await sender.Send(new ListActivitiesQuery(leadId, page, pageSize), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
