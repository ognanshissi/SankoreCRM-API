using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

namespace Sankore.Shared.Infrastructure.Logging;

/// <summary>
/// Reads (or generates) a correlation id from the incoming X-Correlation-Id header,
/// echoes it back in the response, and pushes CorrelationId + TenantId + UserId
/// into the ILogger scope so every log entry within the request carries them.
///
/// Must be placed after UseAuthentication() + UseTenantResolution() so that
/// ICurrentUser and ITenantContext are already populated from the JWT / header.
/// </summary>
public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    private const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, ITenantContext tenant)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N");

        // Echo back so callers can trace their request end-to-end.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        var scope = new Dictionary<string, object?> { ["CorrelationId"] = correlationId };

        if (currentUser.IsAuthenticated)
            scope["UserId"] = currentUser.Id;

        if (tenant.HasTenant)
            scope["TenantId"] = tenant.CurrentTenantId;

        using (logger.BeginScope(scope))
        {
            await next(context);
        }
    }
}
