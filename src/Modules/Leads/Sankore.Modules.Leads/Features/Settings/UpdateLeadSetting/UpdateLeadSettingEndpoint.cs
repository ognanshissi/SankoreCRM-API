namespace Sankore.Modules.Leads.Features.Settings.UpdateLeadSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateLeadSettingEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateLeadSetting(this IEndpointRouteBuilder app)
    {
        app.MapPut("{key}", Handle)
            .WithName("UpdateLeadSetting")
            .WithSummary("Change one Leads parameter")
            .WithDescription(
                "Writes a declared M13 parameter for the current tenant. An unknown key answers "
                + "404 SETTING_UNKNOWN; a value that does not parse as the declared type answers "
                + "400. Audited. Changing lead-retention-months governs an irreversible nightly "
                + "job — read GET lead-settings/retention/preview before lowering it. "
                + "Requires permission: lead:settings:manage.")
            .RequireAuthorization(Permissions.CanManageLeadSettings.Code)
            .Produces<LeadSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        string key, UpdateLeadSettingRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateLeadSettingCommand(key, body.Value), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error == LeadSettingErrors.Unknown
            ? Results.NotFound(new { error = result.Error })
            : Results.BadRequest(new { error = result.Error });
    }
}
