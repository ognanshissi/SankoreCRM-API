namespace Sankore.Modules.Leads.Features.ListDismissals;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ListDismissalsEndpoint
{
    public static IEndpointRouteBuilder MapListDismissals(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/dismissals", Handle)
            .WithName("ListDismissals")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanDismissDuplicate.Code)
            .Produces<IReadOnlyList<DismissalDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListDismissalsQuery(leadId), ct);

        if (!result.IsSuccess)
            return Results.NotFound(new { error = result.Error });

        return Results.Ok(result.Value);
    }
}
