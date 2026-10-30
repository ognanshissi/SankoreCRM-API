namespace Sankore.Modules.Kyc.Features.Settings.ListKycSettings;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class ListKycSettingsEndpoint
{
    internal static IEndpointRouteBuilder MapListKycSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet(string.Empty, Handle)
            .WithName("ListKycSettings")
            .WithSummary("List the KYC parameters of the current tenant")
            .WithDescription(
                "Every declared M02 parameter with the value in force, its type, its description, "
                + "the factory default and an IsDefault flag. A key the tenant has never customised "
                + "answers with its factory value — the same one the module resolves to. "
                + "Requires permission: kyc:settings:manage.")
            // kyc:settings:manage, not kyc:read (product owner, 2026-10-30): kyc:read is an agent's
            // right to work their files, and no agent screen ever read these parameters — only the
            // settings screen does. Opening the alert thresholds and simplified-KYC ceilings to every
            // agent served nobody.
            .RequireAuthorization(Permissions.CanManageKycSettings.Code)
            .Produces<IReadOnlyList<KycSettingDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
        => Results.Ok((await sender.Send(new ListKycSettingsQuery(), ct)).Value);
}
