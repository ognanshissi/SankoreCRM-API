namespace Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ResolveReconciliationGapEndpoint
{
    internal static IEndpointRouteBuilder MapResolveReconciliationGap(this IEndpointRouteBuilder app)
    {
        app.MapPost("gaps/{gapId:guid}/resolve", Handle)
            .WithName("ResolveReconciliationGap")
            .WithSummary("Mark a reconciliation gap as resolved")
            .WithDescription(
                "Closes one gap of the daily CRM / external-system comparison, recording who "
                + "closed it and why. The note is mandatory: a compliance finding closed without "
                + "a reason is untraceable. Resolving records a decision and changes nothing on "
                + "either side — if the divergence is still true, the next night's comparison "
                + "re-opens it as a new gap with a new detection date. A gap belonging to another "
                + "tenant reads as absent (404, never 403). Audited with its author. Requires "
                + "Integration.Reconciliation.Resolve.")
            .RequireAuthorization(Permissions.CanResolveIntegrationReconciliation.Code)
            .Produces<ResolveReconciliationGapResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid gapId, ResolveReconciliationGapRequest request, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new ResolveReconciliationGapCommand(gapId, request?.Note ?? string.Empty), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            // 404 and never 403: a gap of another tenant is absent, and "forbidden" would confirm
            // that a divergence about one of their customers exists.
            IntegrationErrors.GapNotFound => Results.NotFound(new { error = result.Error }),

            // 409 rather than 400: the request was well formed, it arrived late. Somebody else
            // resolved it, or last night's comparison closed it automatically, between the
            // screen's read and this call — the front recovers by reloading.
            IntegrationErrors.GapAlreadyResolved => Results.Conflict(new { error = result.Error }),

            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}

/// <param name="Note">
/// What the operator did about the divergence. Mandatory — see the command.
/// </param>
public sealed record ResolveReconciliationGapRequest(string Note);
