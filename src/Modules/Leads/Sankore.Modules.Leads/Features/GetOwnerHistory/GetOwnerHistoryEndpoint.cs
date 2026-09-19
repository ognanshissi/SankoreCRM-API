namespace Sankore.Modules.Leads.Features.GetOwnerHistory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class GetOwnerHistoryEndpoint
{
    public static IEndpointRouteBuilder MapGetOwnerHistory(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/owner-history", Handle)
            .WithName("GetLeadOwnerHistory")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<OwnerAssignmentDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new GetOwnerHistoryQuery(leadId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
