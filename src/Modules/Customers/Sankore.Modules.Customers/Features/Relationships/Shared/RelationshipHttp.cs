using Microsoft.AspNetCore.Http;
using Sankore.Modules.Customers.Domain;

namespace Sankore.Modules.Customers.Features.Relationships.Shared;

/// <summary>
/// Single mapping from this zone's domain error codes to HTTP status codes.
/// A client (or a related client) outside the caller's perimeter is reported as
/// 404 and never 403, so the response cannot be used to probe for existence.
/// </summary>
internal static class RelationshipHttp
{
    public static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound or CustomerErrors.RelationshipNotFound
            => Results.NotFound(new { error }),
        CustomerErrors.ClientReadOnly or CustomerErrors.ConcurrencyConflict
            => Results.Conflict(new { error }),
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
    };
}
