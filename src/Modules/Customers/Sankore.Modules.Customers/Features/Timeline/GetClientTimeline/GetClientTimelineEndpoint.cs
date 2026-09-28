namespace Sankore.Modules.Customers.Features.Timeline.GetClientTimeline;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class GetClientTimelineEndpoint
{
    public static IEndpointRouteBuilder MapGetClientTimeline(this IEndpointRouteBuilder app)
    {
        app.MapGet("{clientId:guid}/timeline", Handle)
            .WithName("GetClientTimeline")
            .WithSummary("Read the unified timeline of a client")
            .WithDescription(
                "Returns the client's timeline, most recent fact first, paginated. "
                + "Aggregates every module feeding the read model: Customers (lifecycle, transfers, "
                + "merges, segment changes, group memberships) and Leads (the commercial history "
                + "imported at conversion); M02/M03/M04/M08 appear here as soon as they publish, "
                + "with no change to this contract. "
                + "Pass sourceModule=<Customers|Leads|…> and/or entryType=<CLIENT_ACTIVATED|…> to filter. "
                + "Summaries never contain a sensitive value (identity document number, phone, e-mail, "
                + "date of birth) — use the audited reveal endpoint for those. "
                + "Answers 404 both when the client does not exist and when it is outside the caller's "
                + "agency perimeter, so the reply never discloses existence. "
                + "Requires permission: customers:read.")
            .RequireAuthorization(Permissions.CanReadCustomer.Code)
            .Produces<PagedResult<ClientTimelineEntryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender,
        Guid clientId,
        string? sourceModule,
        string? entryType,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var result = await sender.Send(
            new GetClientTimelineQuery(clientId, sourceModule, entryType, page, pageSize), ct);

        if (result.IsFailure)
            return result.Error == CustomerErrors.ClientNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.BadRequest(new { error = result.Error });

        return Results.Ok(result.Value);
    }
}
