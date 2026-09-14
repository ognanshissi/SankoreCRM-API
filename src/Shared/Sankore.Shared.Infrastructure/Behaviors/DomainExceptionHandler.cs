using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Sankore.Shared.Infrastructure.Resources;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// Converts a <see cref="DomainException"/> into a RFC-7807 <see cref="ProblemDetails"/>
/// response (HTTP 422) so clients receive a structured, localised message instead of a 500.
///
/// Translation order:
///   1. If <see cref="DomainException.MessageKey"/> is set, look it up in DomainErrors.{culture}.resx.
///   2. Otherwise fall back to the raw <see cref="Exception.Message"/>.
///
/// The active culture is already set on the thread by LanguageResolutionMiddleware
/// before any handler runs, so IStringLocalizer picks the correct resource file automatically.
/// </summary>
public sealed class DomainExceptionHandler(
    IStringLocalizer<DomainErrors> localizer
) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception exception, CancellationToken ct)
    {
        if (exception is not DomainException domainException)
            return false;

        var message = domainException.MessageKey is not null
            ? localizer[domainException.MessageKey].Value
            : domainException.Message;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Domain rule violation",
            Detail = message,
        };

        ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await ctx.Response.WriteAsJsonAsync(problemDetails, ct);

        return true;
    }
}
