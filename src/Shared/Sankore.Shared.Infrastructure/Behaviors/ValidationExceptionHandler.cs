using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Sankore.Shared.Infrastructure.Behaviors;

/// <summary>
/// Converts a FluentValidation <see cref="ValidationException"/> thrown by
/// <see cref="ValidationBehavior{TRequest,TResponse}"/> into a RFC-7807
/// <see cref="ValidationProblemDetails"/> response (HTTP 422) so clients
/// receive structured, field-level error messages instead of a 500.
///
/// Response shape:
/// {
///   "title": "Validation failed",
///   "status": 422,
///   "errors": {
///     "email":    ["'Email' is not a valid email address."],
///     "password": ["'Password' must not be empty."]
///   }
/// }
/// </summary>
public sealed class ValidationExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext ctx, Exception exception, CancellationToken ct)
    {
        if (exception is not ValidationException validationException)
            return false;

        var errors = validationException.Errors
            .GroupBy(e => e.PropertyName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray());

        var problemDetails = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Validation failed",
        };

        ctx.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        await ctx.Response.WriteAsJsonAsync(problemDetails, ct);

        return true;
    }
}
