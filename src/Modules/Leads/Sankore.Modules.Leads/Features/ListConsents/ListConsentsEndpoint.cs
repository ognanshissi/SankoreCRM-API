namespace Sankore.Modules.Leads.Features.ListConsents;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Kernel;

public static class ListConsentsEndpoint
{
    public static IEndpointRouteBuilder MapListConsents(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/consents", Handle)
            .WithName("ListConsents")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanRecordConsent.Code)
            .Produces<IReadOnlyList<ConsentDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new ListConsentsQuery(leadId), ct);

        if (!result.IsSuccess)
            return Results.NotFound(new { error = result.Error });

        return Results.Ok(result.Value);
    }
}
