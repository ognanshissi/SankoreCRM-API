namespace Sankore.Modules.Leads.Features.Settings.PreviewRetention;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class PreviewRetentionEndpoint
{
    internal static IEndpointRouteBuilder MapPreviewRetention(this IEndpointRouteBuilder app)
    {
        app.MapGet("retention/preview", Handle)
            .WithName("PreviewLeadRetention")
            .WithSummary("What tonight's lead anonymisation would destroy")
            .WithDescription(
                "The retention period in force, the cutoff, how many leads are due now, the "
                + "per-run ceiling and the closure date of the oldest due lead. Computed with the "
                + "SAME selection the nightly job runs, so the figure approved here is the figure "
                + "destroyed. Answers 409 LEAD_RETENTION_NOT_CONFIGURED when the period is zero "
                + "or negative — the run skips in that case rather than anonymising a lead the "
                + "moment it closes. Requires permission: lead:settings:manage.")
            // Not lead:read: this answers "how many records is tonight about to destroy", which is
            // an administrator's question about an irreversible operation.
            .RequireAuthorization(Permissions.CanManageLeadSettings.Code)
            .Produces<RetentionPreviewDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new PreviewRetentionQuery(), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Conflict(new { error = result.Error });
    }
}
