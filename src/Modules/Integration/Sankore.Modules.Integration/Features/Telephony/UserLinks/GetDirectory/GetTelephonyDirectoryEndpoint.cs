namespace Sankore.Modules.Integration.Features.Telephony.UserLinks.GetDirectory;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetTelephonyDirectoryEndpoint
{
    internal static IEndpointRouteBuilder MapGetTelephonyDirectory(this IEndpointRouteBuilder app)
    {
        app.MapGet("directory", Handle)
            .WithName("GetTelephonyDirectory")
            .WithSummary("The provider's user accounts, with the CRM account each one looks like")
            .WithDescription(
                "Reads the telephony provider's directory LIVE and proposes a pairing per "
                + "account, matched on the e-mail address. A proposal is never applied: confirm "
                + "it with POST telephony/user-links. An address several CRM accounts answer to "
                + "yields no proposal and is listed in ambiguousEmails — picking one would be "
                + "picking whose calls get attributed to whom. Already-linked accounts come back "
                + "with linkedCrmUserId set. Answers 422 TELEPHONY_DISABLED when the tenant has "
                + "no active telephony connection, 422 "
                + "INTEGRATION_CAPABILITY_NOT_SUPPORTED when its provider publishes no directory, "
                + "and 503 INTEGRATION_UNAVAILABLE when the provider could not be reached — the "
                + "three are distinct repairs. "
                + "Requires permission: Telephony.Settings.Manage.")
            .RequireAuthorization(Permissions.CanManageTelephonySettings.Code)
            .Produces<TelephonyDirectoryDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetTelephonyDirectoryQuery(), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        return result.Error switch
        {
            // A provider that cannot be reached is the one failure here worth retrying, so it is
            // the one that answers 5xx: a front end told 422 would show the administrator a
            // configuration error for an outage.
            IntegrationErrors.Unavailable =>
                Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),
            TelephonyErrors.Disabled
                or IntegrationErrors.CapabilityNotSupported =>
                Results.UnprocessableEntity(new { error = result.Error }),
            _ => Results.UnprocessableEntity(new { error = result.Error }),
        };
    }
}
