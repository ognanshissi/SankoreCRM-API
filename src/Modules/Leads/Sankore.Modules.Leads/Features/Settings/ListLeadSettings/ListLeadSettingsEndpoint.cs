namespace Sankore.Modules.Leads.Features.Settings.ListLeadSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListLeadSettingsEndpoint
{
    internal static IEndpointRouteBuilder MapListLeadSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListLeadSettings")
            .WithSummary("List the Leads parameters of the current tenant")
            .WithDescription(
                "Every declared M13 parameter with the value in force, its type, its description, "
                + "the factory default and an IsDefault flag. A key the tenant has never "
                + "customised answers with its factory value — the same one the module resolves "
                + "to. Requires permission: lead:read.")
            // lead:read, not lead:settings:manage: a screen explaining a retention period to an
            // agent must not require the right to change it. M02 made the same arrangement for
            // its ceilings.
            .RequireAuthorization(Permissions.CanReadLead.Code)
            .Produces<IReadOnlyList<LeadSettingDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ListLeadSettingsQuery(), ct)).Value);
}
