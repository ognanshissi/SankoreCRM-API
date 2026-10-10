namespace Sankore.Modules.Leads.Features.CustomerLink.GetCustomerMatches;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

internal static class GetCustomerMatchesEndpoint
{
    public static IEndpointRouteBuilder MapGetCustomerMatches(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/customer-matches", Handle)
            .WithName("GetLeadCustomerMatches")
            .WithTags("Leads")
            .WithSummary("Existing customers this lead might belong to (BNF-B-22)")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<CustomerMatchDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(Guid leadId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetCustomerMatchesQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
