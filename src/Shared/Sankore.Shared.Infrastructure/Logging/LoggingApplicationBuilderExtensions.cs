using Microsoft.AspNetCore.Builder;

namespace Sankore.Shared.Infrastructure.Logging;

public static class LoggingApplicationBuilderExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationIdMiddleware>();
}
