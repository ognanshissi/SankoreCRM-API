using System.Globalization;
using Microsoft.AspNetCore.Http;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Models;

namespace Sankore.Shared.Infrastructure.Localization;

/// <summary>
/// Resolves the active language for each request and stores it in
/// HttpContext.Items so HttpLanguageContext can serve it without re-parsing.
///
/// Resolution priority (descending authority):
///   1. JWT "lang" claim       — user preference, signed, non-falsifiable.
///   2. Accept-Language header — client/browser preference (RFC 4647).
///   3. Tenant default         — TenantInfo.DefaultLanguage from the tenant store (cache hit).
///   4. Languages.Fr           — hardcoded system default.
///
/// Must be placed AFTER UseAuthentication() so JWT claims are already populated,
/// and after UseTenantResolution() so the resolved tenant ID is in HttpContext.Items.
/// </summary>
public sealed class LanguageResolutionMiddleware(RequestDelegate next, ITenantStore tenantStore)
{
    private static readonly Dictionary<string, Languages> SupportedPrefixes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = Languages.Fr,
            ["en"] = Languages.En,
        };

    private static readonly Dictionary<Languages, CultureInfo> CultureMap = new()
    {
        [Languages.Fr] = CultureInfo.GetCultureInfo("fr"),
        [Languages.En] = CultureInfo.GetCultureInfo("en"),
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        var lang = await ResolveAsync(ctx);
        ctx.Items[LanguageKey.ResolvedLanguageKey] = lang;

        // Set thread culture so IStringLocalizer<T> picks the correct resource file.
        var culture = CultureMap[lang];
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        await next(ctx);
    }

    private async Task<Languages> ResolveAsync(HttpContext ctx)
    {
        // 1. JWT lang claim — set by JwtTokenService from user.PreferredLanguage ?? tenant.DefaultLanguage.
        var claim = ctx.User.FindFirst("lang")?.Value;
        if (claim is not null && TryParse(claim, out var fromClaim))
            return fromClaim;

        // 2. Accept-Language header — e.g. "fr-FR,fr;q=0.9,en;q=0.8"
        var acceptLanguage = ctx.Request.Headers.AcceptLanguage.ToString();
        if (!string.IsNullOrWhiteSpace(acceptLanguage))
        {
            foreach (var segment in acceptLanguage.Split(','))
            {
                var tag = segment.Split(';')[0].Trim();  // strip q-value
                var prefix = tag.Split('-')[0];          // "fr-FR" → "fr"
                if (TryParse(prefix, out var fromHeader))
                    return fromHeader;
            }
        }

        // 3. Tenant default — cheap Redis cache hit; tenant is already validated upstream.
        if (ctx.Items.TryGetValue(TenantKey.ResolvedTenantKey, out var tenantIdObj)
            && tenantIdObj is Guid tenantId)
        {
            var tenantInfo = await tenantStore.GetAsync(tenantId, ctx.RequestAborted);
            if (tenantInfo is not null && TryParse(tenantInfo.DefaultLanguage, out var fromTenant))
                return fromTenant;
        }

        // 4. Hardcoded system default.
        return Languages.Fr;
    }

    private static bool TryParse(string value, out Languages result)
        => SupportedPrefixes.TryGetValue(value, out result);
}
