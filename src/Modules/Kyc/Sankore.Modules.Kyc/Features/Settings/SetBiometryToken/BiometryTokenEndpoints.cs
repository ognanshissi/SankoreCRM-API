namespace Sankore.Modules.Kyc.Features.Settings.SetBiometryToken;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Kyc.Infrastructure.Biometry;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// The token's two routes: write it, and ask whether one is stored.
///
/// There is deliberately no route that returns the value. A screen needs to know "is this
/// configured, and is it the one I think it is" — the vault's masked hint answers both, and
/// anything more would turn an administration page into a way to read a credential back out.
/// </summary>
internal static class BiometryTokenEndpoints
{
    internal static IEndpointRouteBuilder MapBiometryToken(this IEndpointRouteBuilder app)
    {
        app.MapPut("biometry-token", Set)
            .WithName("SetKycBiometryToken")
            .WithSummary("Store the tenant's biometric service token")
            .WithDescription(
                "Writes the bearer token HttpBiometryClient sends to the external biometric "
                + "service, into the secrets vault, for the CURRENT tenant. Without it every "
                + "verification answers BIOMETRY_NOT_CONFIGURED and the KYC file stays in "
                + "Verifying for the replay job — the client is never rejected over our own "
                + "configuration. The value is write-only: it is never returned by any endpoint, "
                + "never logged, and recorded as \"***\" in the audit trail. It cannot be set "
                + "through configuration — Kyc:Biometry has no token key. Audited. "
                + "Requires permission: kyc:settings:manage.")
            .RequireAuthorization(Permissions.CanManageKycSettings.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        app.MapGet("biometry-token", Status)
            .WithName("GetKycBiometryTokenStatus")
            .WithSummary("Whether a biometric service token is stored, and its masked hint")
            .WithDescription(
                "Answers whether this tenant has a token and returns the vault's masked hint — "
                + "enough to tell two tokens apart while rotating, never enough to use one. The "
                + "value itself is not retrievable. Requires permission: kyc:settings:manage.")
            .RequireAuthorization(Permissions.CanManageKycSettings.Code)
            .Produces<BiometryTokenStatusDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Set(
        SetBiometryTokenRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new SetBiometryTokenCommand(req.Token), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.BadRequest(new { error = result.Error });
    }

    /// <summary>
    /// Reads the hint directly rather than through MediatR: there is no decision to take, no
    /// transaction to open and nothing to audit about asking whether a setting exists.
    /// </summary>
    private static async Task<IResult> Status(
        ISecretsModule secrets,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var hint = await secrets.GetHintAsync(
            BiometrySecrets.TokenKey(tenant.CurrentTenantId), ct);

        return Results.Ok(new BiometryTokenStatusDto(
            IsConfigured: hint is not null,
            MaskedValue: hint?.MaskedValue,
            ExpiresAt: hint?.ExpiresAt));
    }
}

/// <summary>The token travels in the body, never in the route or the query string.</summary>
internal sealed record SetBiometryTokenRequest(string Token);
