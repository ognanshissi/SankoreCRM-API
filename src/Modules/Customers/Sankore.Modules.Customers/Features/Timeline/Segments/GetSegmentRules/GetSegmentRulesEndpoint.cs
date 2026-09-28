namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentRules;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetSegmentRulesEndpoint
{
    public static IEndpointRouteBuilder MapGetSegmentRules(this IEndpointRouteBuilder app)
    {
        app.MapGet("segments/rules", Handle)
            .WithName("GetClientSegmentRules")
            .WithSummary("Read the tenant's segmentation rules")
            .WithDescription(
                "Returns the rules stored in the tenant setting segment-rules-json, ordered by "
                + "ascending priority — the exact order the nightly job evaluates them in, first match "
                + "wins. Each rule carries isEvaluable: false means the rule is stored but NEVER "
                + "applied because it needs outstanding balances or product holdings from M03 "
                + "(Savings) / M04 (Credit), which expose no contract yet "
                + "(notEvaluableReason = OUTSTANDING_DATA_UNAVAILABLE). "
                + "isValid: false with a parseError means the stored JSON is malformed and NO rule runs. "
                + "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<SegmentRulesDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetSegmentRulesQuery(), ct);

        return result.IsFailure
            ? Results.BadRequest(new { error = result.Error })
            : Results.Ok(result.Value);
    }
}
