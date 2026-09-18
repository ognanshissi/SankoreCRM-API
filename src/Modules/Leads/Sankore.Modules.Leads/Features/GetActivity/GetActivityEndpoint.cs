namespace Sankore.Modules.Leads.Features.GetActivity;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.ListActivities;
using Sankore.Shared.Kernel;

public static class GetActivityEndpoint
{
    public static IEndpointRouteBuilder MapGetActivity(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/activities/{activityId:guid}", Handle)
            .WithName("GetLeadActivity")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<ActivityDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        Guid activityId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetActivityQuery(leadId, activityId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
