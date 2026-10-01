namespace Sankore.Modules.Kyc.Features.Settings.UpdateKycSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateKycSettingEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateKycSetting(this IEndpointRouteBuilder app)
    {
        app.MapPut("{key}", Handle)
            .WithName("UpdateKycSetting")
            .WithSummary("Change one KYC parameter of the current tenant")
            .WithDescription(
                "Writes a single M02 parameter. The value is checked against the declared type AND "
                + "against a range: a ceiling must be positive, a percentage must be 1..100, a flow "
                + "window 1..365 days, a review periodicity 1..30 years, the face-match attempts "
                + "1..10. An unknown key answers 404 KYC_SETTING_UNKNOWN; a well-typed but "
                + "meaningless value answers 400 KYC_SETTING_VALUE_OUT_OF_RANGE. Changing a "
                + "simplified-tier ceiling drops the cached ceilings of every customer of the tenant, "
                + "so it takes effect immediately. Audited. "
                + "Requires permission: kyc:settings:manage.")
            .RequireAuthorization(Permissions.CanManageKycSettings.Code)
            .Produces<KycSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        string key,
        UpdateKycSettingRequest req,
        ISender sender,
        CancellationToken ct)
    {
        // The key comes from the route and never from the body: a mismatch between the two would let
        // a caller write a key other than the one the URL — and any log of it — names.
        var result = await sender.Send(new UpdateKycSettingCommand(key, req.Value), ct);

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        return result.Error switch
        {
            KycErrors.SettingUnknown => Results.NotFound(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}
