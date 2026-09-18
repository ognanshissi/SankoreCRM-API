namespace Sankore.Modules.Leads.Features.GetLeadTimeline;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetLeadTimelineEndpoint
{
    public static IEndpointRouteBuilder MapGetLeadTimeline(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/timeline", Handle)
            .WithName("GetLeadTimeline")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<TimelineEvent>>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadTimelineQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound();
    }
}
