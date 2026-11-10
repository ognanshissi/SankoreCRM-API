namespace Sankore.Shared.Infrastructure.Tenants;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Kernel;

/// <summary>
/// Keeps the KYC routes read-only for an institution whose KYC is switched off
/// (docs/kyc-par-institution.md, product owner 2026-11-10: « dossiers en consultation seule »).
/// GET and HEAD pass; any other verb answers <c>409 KYC_DISABLED</c>, so the front-end says why
/// rather than a generic refusal. A route that only READS by POST must be exempted by name.
/// </summary>
public sealed class KycInForceEndpointFilter(Func<HttpContext, bool>? exempt = null) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
            || exempt?.Invoke(http) == true)
            return await next(context);

        var tenant = http.RequestServices.GetRequiredService<ITenantContext>();
        var kyc = http.RequestServices.GetRequiredService<ITenantKyc>();
        if (await kyc.IsInForceAsync(tenant.CurrentTenantId, http.RequestAborted))
            return await next(context);

        return KycDisabledResult();
    }

    public static IResult KycDisabledResult() => Results.Json(
        new
        {
            error = "Le KYC est désactivé pour votre institution : les dossiers sont en consultation seule.",
            code = TenantKycErrors.Disabled,
        },
        statusCode: StatusCodes.Status409Conflict);
}

public static class KycInForceEndpointFilterExtensions
{
    /// <summary>Writes refused while the institution's KYC is switched off; reads stay open.</summary>
    public static TBuilder RequireKycInForce<TBuilder>(this TBuilder builder, Func<HttpContext, bool>? exempt = null)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(new KycInForceEndpointFilter(exempt));
}
