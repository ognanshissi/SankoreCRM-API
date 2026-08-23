namespace Sankore.Shared.Infrastructure.Behaviors;

using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// First behavior in the pipeline (registered first = runs outermost).
/// Logs entry/exit and duration of every MediatR request with structured
/// fields: Module, RequestName, Outcome, ElapsedMs.
/// Domain failures (Result.IsFailure) are logged as Warning; exceptions as Error.
/// CorrelationId, TenantId and UserId are already in the log scope thanks to
/// CorrelationIdMiddleware — no need to repeat them here.
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
        var module = ExtractModule(typeof(TRequest));
        var requestName = typeof(TRequest).Name;
        var sw = Stopwatch.StartNew();

        logger.LogInformation(
            "Handling {Module}.{RequestName}",
            module, requestName);

        try
        {
            var response = await next();
            sw.Stop();

            var (outcome, error) = response switch
            {
                Result { IsFailure: true } r => ("FAILURE", r.Error),
                _ => ("SUCCESS", (string?)null)
            };

            if (outcome == "FAILURE")
                logger.LogWarning(
                    "Handled {Module}.{RequestName} → {Outcome} in {ElapsedMs} ms | {Error}",
                    module, requestName, outcome, sw.ElapsedMilliseconds, error);
            else
                logger.LogInformation(
                    "Handled {Module}.{RequestName} → {Outcome} in {ElapsedMs} ms",
                    module, requestName, outcome, sw.ElapsedMilliseconds);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex,
                "Exception in {Module}.{RequestName} after {ElapsedMs} ms",
                module, requestName, sw.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Extracts the module name from the request's namespace.
    /// "Sankore.Modules.Leads.Features.*" → "Leads"
    /// Falls back to "Core" for requests outside the Modules namespace.
    /// </summary>
    private static string ExtractModule(Type requestType)
    {
        var parts = requestType.Namespace?.Split('.') ?? [];
        var idx = Array.IndexOf(parts, "Modules");
        return idx >= 0 && idx + 1 < parts.Length ? parts[idx + 1] : "Core";
    }
}
