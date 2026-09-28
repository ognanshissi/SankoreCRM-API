using Microsoft.AspNetCore.Http;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Features.ContactPoints.Shared;

/// <summary>
/// Single mapping from this zone's domain error codes to HTTP status codes.
/// Out-of-perimeter clients are deliberately reported as 404 and never 403:
/// a 403 would confirm the existence of a client the caller may not see.
/// </summary>
internal static class ContactPointHttp
{
    public static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound or CustomerErrors.ContactPointNotFound
            => Results.NotFound(new { error }),
        CustomerErrors.ClientReadOnly
            or CustomerErrors.LastPhoneRequired
            or CustomerErrors.ConcurrencyConflict
            => Results.Conflict(new { error }),
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
    };
}
