namespace Sankore.Modules.Kyc.Features.Settings.GetKycSetting;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycSettingEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycSetting(this IEndpointRouteBuilder app)
    {
        app.MapGet("{key}", Handle)
            .WithName("GetKycSetting")
            .WithSummary("Read one KYC parameter")
            .WithDescription(
                "A single M02 parameter. An unknown key answers 404 KYC_SETTING_UNKNOWN rather than "
                + "an empty value. Requires permission: kyc:read.")
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycSettingDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(string key, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetKycSettingQuery(key), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
