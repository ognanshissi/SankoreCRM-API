namespace Sankore.Modules.Customers.Features.Household.Shared;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// The one mapping from this zone's codes to HTTP statuses (BNF-B-13). A client outside the
/// caller's perimeter is 404 and never 403, so the response cannot be used to probe for existence —
/// the module's existing rule.
/// </summary>
internal static class HouseholdHttp
{
    public static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound or CustomerErrors.RelationshipNotFound
            => Results.NotFound(new { error }),

        CustomerErrors.ClientReadOnly
        or CustomerErrors.ConcurrencyConflict
        or CustomerErrors.RelationshipClosed
        or CustomerErrors.HouseholdAlreadyHasSpouse
            => Results.Conflict(new { error }),

        // A dependency was unreachable, not a bad request: a client should retry rather than be
        // shown a validation error they cannot act on.
        CustomerErrors.ComplianceSettingsUnavailable
            => Results.Problem(
                title: "A dependency is unavailable", detail: error, statusCode: 503),

        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}
