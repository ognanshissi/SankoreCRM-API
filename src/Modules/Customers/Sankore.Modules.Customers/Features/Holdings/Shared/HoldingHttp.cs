namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// The one mapping from this zone's codes to HTTP statuses (BNF-B-14, BNF-B-15).
/// </summary>
internal static class HoldingHttp
{
    public static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound
        or CustomerErrors.HoldingNotFound
        or CustomerErrors.ProductLineNotFound
            => Results.NotFound(new { error }),

        // State conflicts, not malformed requests: the caller asked for something reasonable that
        // the holding's current state refuses, and a retry after a change would succeed.
        CustomerErrors.HoldingReadOnly
        or CustomerErrors.HoldingNotEditable
        or CustomerErrors.HoldingInvalidTransition
        or CustomerErrors.HoldingSyncInProgress
        or CustomerErrors.HoldingAlreadyHeld
        or CustomerErrors.ClientReadOnly
        or CustomerErrors.ConcurrencyConflict
            => Results.Conflict(new { error }),

        CustomerErrors.ProductLineUnavailable
            => Results.Problem(
                title: "A dependency is unavailable", detail: error, statusCode: 503),

        // Everything the designation rules refuse is a 422 and not a 400: the body was
        // well-formed, and what failed is a business rule the agent can see and fix.
        CustomerErrors.DesignationRoleNotAllowed
        or CustomerErrors.DesignationRequiredRoleMissing
        or CustomerErrors.DesignationTooMany
        or CustomerErrors.DesignationSharesMustTotal100
        or CustomerErrors.DesignationInvalidShare
        or CustomerErrors.DesignationInvalidRank
        or CustomerErrors.DesignationSubjectInvalid
        or CustomerErrors.DesignationDuplicateSubject
        or CustomerErrors.DesignationSubjectNotInHousehold
        or CustomerErrors.DesignationSubjectNotFound
        or CustomerErrors.SignatureRequired
            => Results.UnprocessableEntity(new { error }),

        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}
