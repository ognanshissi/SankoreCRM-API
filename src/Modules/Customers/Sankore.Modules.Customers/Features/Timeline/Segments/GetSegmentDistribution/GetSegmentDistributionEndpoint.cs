namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentDistribution;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetSegmentDistributionEndpoint
{
    public static IEndpointRouteBuilder MapGetSegmentDistribution(this IEndpointRouteBuilder app)
    {
        app.MapGet("segments", Handle)
            .WithName("GetClientSegmentDistribution")
            .WithSummary("Client count per commercial segment")
            .WithDescription(
                "Returns how many live clients sit in each segment, plus each segment's share. "
                + "Archived and merged clients are excluded. The caller's agency perimeter applies, "
                + "so two users of different branches legitimately see different totals. "
                + "Clients no rule has classified yet appear as a last bucket with a null segmentCode. "
                + "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<SegmentDistributionDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetSegmentDistributionQuery(), ct);

        return result.IsFailure
            ? Results.BadRequest(new { error = result.Error })
            : Results.Ok(result.Value);
    }
}
