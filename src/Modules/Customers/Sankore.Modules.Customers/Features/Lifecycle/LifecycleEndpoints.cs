namespace Sankore.Modules.Customers.Features.Lifecycle;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Modules.Customers.Features.Lifecycle.ArchiveClient;
using Sankore.Modules.Customers.Features.Lifecycle.AssignAdvisor;
using Sankore.Modules.Customers.Features.Lifecycle.GetStatusHistory;
using Sankore.Modules.Customers.Features.Lifecycle.ReactivateClient;
using Sankore.Modules.Customers.Features.Lifecycle.SuspendClient;
using Sankore.Modules.Customers.Features.Lifecycle.TransferClient;

/// <summary>
/// Endpoints of the client lifecycle (US-M01-BE-13 → 16), all mounted under
/// <c>api/v1/clients/{clientId}</c>.
///
/// DELIBERATELY ABSENT: there is no DELETE endpoint anywhere in this zone — nor
/// anywhere else in the module. A client record is never physically removed:
/// <c>POST .../archive</c> is the soft end of life (the row becomes read-only and
/// keeps its whole history), and erasing personal data is a separate, audited
/// anonymization flow owned by the Compliance zone
/// (<c>POST clients/{clientId}/anonymize</c>, US-M01-BE-29) which is gated by the
/// retention period and by the KYC retention clearance. Adding a physical delete
/// here would destroy the status history, the timeline and the audit trail that
/// the regulator expects to find.
/// </summary>
public static class LifecycleEndpoints
{
    public static IEndpointRouteBuilder MapLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("clients").WithTags("Client Lifecycle");

        return group
            .MapSuspendClient()
            .MapReactivateClient()
            .MapArchiveClient()
            .MapAssignAdvisor()
            .MapTransferClient()
            .MapGetStatusHistory();
    }
}

/// <summary>
/// Maps this zone's error codes onto HTTP statuses, in one place so the six
/// endpoints cannot drift apart.
/// </summary>
internal static class LifecycleHttp
{
    /// <summary>Not a constant of <c>CustomerErrors</c> yet — see ArchiveClientHandler.</summary>
    private const string ClientHasActiveCommitments = "CLIENT_HAS_ACTIVE_COMMITMENTS";

    internal static IResult ToProblem(string error) => error switch
    {
        // A record outside the caller's agency perimeter is reported as missing, so
        // the perimeter never reveals that it exists.
        CustomerErrors.ClientNotFound =>
            Results.Problem(error, statusCode: StatusCodes.Status404NotFound),

        // The caller is authenticated and holds the permission, but not over this
        // agency — that is a genuine 403, not a validation error.
        CustomerErrors.AgencyOutOfScope =>
            Results.Problem(error, statusCode: StatusCodes.Status403Forbidden),

        // State conflicts: the request was well formed, the record just is not in a
        // state that accepts it.
        CustomerErrors.InvalidStatusTransition
            or CustomerErrors.ClientReadOnly
            or CustomerErrors.ConcurrencyConflict
            or ClientHasActiveCommitments =>
            Results.Problem(error, statusCode: StatusCodes.Status409Conflict),

        // Everything else (REASON_REQUIRED, ADVISOR_NOT_ELIGIBLE, …) is a bad request.
        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest),
    };
}
