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
                + "Requires permission: kyc:read.")
            // kyc:read, not kyc:settings:manage: these are the ceilings and periodicities a KYC
            // screen explains to an agent. Needing the right to CHANGE a ceiling in order to SEE it
            // would push the amounts back into the front, which is what KYC-B-06 undid.
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
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
