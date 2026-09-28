namespace Sankore.Modules.Customers.Features.Timeline;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Features.Timeline.GetClientTimeline;
using Sankore.Modules.Customers.Features.Timeline.Loyalty.GetLoyaltyScore;
using Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentDistribution;
using Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentRules;
using Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;

/// <summary>
/// Area aggregator of the Timeline zone (US-M01-BE-26/27/28).
///
/// All four features hang off the same <c>clients</c> prefix as the Clients zone — several
/// groups may share a prefix, they are independent route sets. The literal <c>segments</c>
/// routes cannot be shadowed by <c>clients/{clientId}</c> because that parameter carries a
/// <c>:guid</c> constraint.
/// </summary>
public static class TimelineEndpoints
{
    public static IEndpointRouteBuilder MapTimelineEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients").WithTags("Client Timeline & Segments");

        return group
            .MapGetClientTimeline()
            .MapGetSegmentDistribution()
            .MapGetSegmentRules()
            .MapUpdateSegmentRules()
            .MapGetLoyaltyScore();
    }
}
