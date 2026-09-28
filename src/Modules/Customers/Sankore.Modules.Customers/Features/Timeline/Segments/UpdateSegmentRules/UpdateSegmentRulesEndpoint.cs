namespace Sankore.Modules.Customers.Features.Timeline.Segments.UpdateSegmentRules;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class UpdateSegmentRulesEndpoint
{
    public static IEndpointRouteBuilder MapUpdateSegmentRules(this IEndpointRouteBuilder app)
    {
        app.MapPut("segments/rules", Handle)
            .WithName("UpdateClientSegmentRules")
            .WithSummary("Replace the tenant's segmentation rules")
            .WithDescription(
                "Replaces the whole rule set (a rule set is an ordered decision list, so it is written "
                + "as a set, never patched rule by rule). Rules are evaluated by ascending priority and "
                + "the first match wins; a rule with no criterion matches everyone and is how a "
                + "catch-all segment is expressed. "
                + "A rule with requiresOutstandingData=true is accepted and stored but stays INACTIVE "
                + "until M03 (Savings) / M04 (Credit) expose a contract — the response reports how many. "
                + "Changes take effect on the next nightly run of the segmentation job. "
                + "Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<UpdateSegmentRulesResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        UpdateSegmentRulesRequest body,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateSegmentRulesCommand(body.Rules ?? []), ct);

        if (result.IsFailure)
            return result.Error == CustomerErrors.SettingUnknown
                ? Results.NotFound(new { error = result.Error })
                : Results.BadRequest(new { error = result.Error });

        return Results.Ok(result.Value);
    }
}

/// <summary>Request body of <c>PUT clients/segments/rules</c>.</summary>
public sealed record UpdateSegmentRulesRequest(IReadOnlyList<SegmentRuleDefinition>? Rules);
