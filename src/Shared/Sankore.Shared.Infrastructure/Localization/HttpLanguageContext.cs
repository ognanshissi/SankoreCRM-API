using Microsoft.AspNetCore.Http;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.Models;

namespace Sankore.Shared.Infrastructure.Localization;

/// <summary>
/// Reads the language resolved by <see cref="LanguageResolutionMiddleware"/>
/// from HttpContext.Items. Registered as Scoped — resolved once per HTTP request.
/// Falls back to Languages.Fr if called outside an HTTP context (background jobs).
/// </summary>
public sealed class HttpLanguageContext(IHttpContextAccessor accessor) : ILanguageContext
{
    public Languages CurrentLanguage
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx is not null
                && ctx.Items.TryGetValue(LanguageKey.ResolvedLanguageKey, out var item)
                && item is Languages lang)
                return lang;

            return Languages.Fr;
        }
    }
}
