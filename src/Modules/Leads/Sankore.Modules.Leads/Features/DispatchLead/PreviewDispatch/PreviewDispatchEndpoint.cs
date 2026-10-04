namespace Sankore.Modules.Leads.Features.DispatchLead.PreviewDispatch;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// <c>GET leads/{leadId}/dispatch-preview</c> — the ranked candidates a dispatch would consider.
///
/// A GET on purpose, and gated on the READ permission rather than <c>lead:assign</c>: looking at
/// who would receive a lead changes nothing, and requiring the right to assign would keep the
/// information from the people who open the screen to decide whether to.
/// </summary>
public static class PreviewDispatchEndpoint
{
    public static IEndpointRouteBuilder MapPreviewDispatch(this IEndpointRouteBuilder app)
    {
        app.MapGet("{leadId:guid}/dispatch-preview", Handle)
            .WithName("PreviewLeadDispatch")
            .WithTags("Leads")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<DispatchPreviewResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new PreviewDispatchQuery(leadId), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        // LEAD_NOT_FOUND is a 404; a closed lead is a 422 — the lead exists, there is simply
        // nothing to preview for it.
        return result.Error == "LEAD_NOT_FOUND"
            ? Results.NotFound(new { error = result.Error })
            : Results.Problem(
                title: "Dispatch preview unavailable",
                detail: result.Error,
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }
}
