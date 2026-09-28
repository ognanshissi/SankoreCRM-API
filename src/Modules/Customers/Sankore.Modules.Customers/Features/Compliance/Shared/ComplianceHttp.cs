namespace Sankore.Modules.Customers.Features.Compliance.Shared;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Customers.Domain;

/// <summary>
/// Single mapping from this zone's error codes to HTTP status codes.
/// <para>
/// A client the caller may not see, an export that is not theirs and a bad download token all
/// answer <b>404</b>: a 403 would confirm the existence of the resource, and for a download
/// token it would turn the endpoint into an oracle a brute-force script could walk.
/// </para>
/// </summary>
internal static class ComplianceHttp
{
    public static IResult ToProblem(string? error) => error switch
    {
        CustomerErrors.ClientNotFound
            or CustomerErrors.ExportNotFound
            => Results.NotFound(new { error }),

        CustomerErrors.SettingUnknown
            => Results.NotFound(new { error }),

        CustomerErrors.RetentionNotReached
            or CustomerErrors.KycRetentionNotCleared
            or CustomerErrors.ExportLinkExpired
            or CustomerErrors.ClientReadOnly
            => Results.Conflict(new { error }),

        _ => Results.Problem(error, statusCode: StatusCodes.Status400BadRequest)
    };
}
