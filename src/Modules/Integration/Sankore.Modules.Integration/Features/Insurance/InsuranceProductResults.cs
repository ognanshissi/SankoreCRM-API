namespace Sankore.Modules.Integration.Features.Insurance;

using Microsoft.AspNetCore.Http;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The error code → HTTP status mapping of the product slices, in one place.
///
/// <para>
/// Shared rather than repeated in five endpoints, because the one rule that must never vary is
/// the first line: an unknown product and another tenant's product are the <b>same 404</b>. A
/// single endpoint answering 403 would confirm that the id names a real catalogue entry, and a
/// catalogue entry names an insurer — which tells an outsider that another institution is hosted
/// here and who it distributes for.
/// </para>
///
/// <para>
/// Matched on a PREFIX because several of these codes travel with a detail appended
/// (<c>CODE: why</c>), which is how a refusal reaches an administrator with something to act on.
/// </para>
/// </summary>
internal static class InsuranceProductResults
{
    internal static IResult Problem(string? error)
    {
        var status = error switch
        {
            null => StatusCodes.Status422UnprocessableEntity,

            _ when Is(error, IntegrationErrors.InsuranceProductNotFound)
                   || Is(error, IntegrationErrors.ConnectionNotFound)
                => StatusCodes.Status404NotFound,

            _ when Is(error, IntegrationErrors.InsuranceProductAlreadyExists)
                => StatusCodes.Status409Conflict,

            _ when Is(error, IntegrationErrors.ConcurrencyConflict)
                => StatusCodes.Status409Conflict,

            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return Results.Problem(error, statusCode: status);
    }

    private static bool Is(string error, string code)
        => error.StartsWith(code, StringComparison.Ordinal);
}
