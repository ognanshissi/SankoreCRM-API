namespace Sankore.Modules.Leads.Features.Settings.GetLeadSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetLeadSettingEndpoint
{
    internal static IEndpointRouteBuilder MapGetLeadSetting(this IEndpointRouteBuilder app)
    {
        app.MapGet("{key}", Handle)
            .WithName("GetLeadSetting")
            .WithSummary("Read one Leads parameter")
            .WithDescription(
                "A single M13 parameter. An unknown key answers 404 SETTING_UNKNOWN rather than "
                + "an empty value. Requires permission: lead:read.")
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<LeadSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(string key, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetLeadSettingQuery(key), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
