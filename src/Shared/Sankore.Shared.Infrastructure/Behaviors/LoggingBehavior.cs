namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;

/// <summary>
/// First behavior in the pipeline (registered first = runs outermost).
/// Logs entry/exit and duration of every request handled through MediatR,
/// across all modules, with structured fields for observability tooling
/// (Serilog + OpenTelemetry sinks configured in the Bootstrapper).
/// </summary>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var sw = Stopwatch.StartNew();

        logger.LogInformation("Handling {RequestName}", requestName);

        try
        {
            var response = await next();
            sw.Stop();

            logger.LogInformation(
                "Handled {RequestName} in {ElapsedMs} ms",
                requestName, sw.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "Unhandled exception in {RequestName} after {ElapsedMs} ms",
                requestName, sw.ElapsedMilliseconds);
            throw;
        }
    }
}
