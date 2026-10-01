namespace Sankore.Modules.Kyc.Features.Limits.GetCaps;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class GetKycCapsEndpoint
{
    internal static IEndpointRouteBuilder MapGetKycCaps(this IEndpointRouteBuilder app)
    {
        app.MapGet("by-customer/{customerId:guid}/caps", Handle)
            .WithName("GetKycCaps")
            .WithSummary("Read the operation ceilings in force for a customer")
            .WithDescription(
                "The simplified-tier ceilings as the tenant has configured them, with the currency "
                + "they are expressed in and the width of the rolling flow window. Amounts already "
                + "consumed are reported as NULL with a reason code, not as zero: no module owns an "
                + "account or a transaction yet, and a zero would read as 'nothing consumed'. "
                + "An uncapped (full KYC) customer gets every ceiling null. "
                + "Requires permission: kyc:read.")
            // kyc:read: a teller who may see where a file stands may see what it allows. Reading a
            // ceiling changes nothing, so it does not deserve kyc:manage.
            .RequireAuthorization(Permissions.CanReadKycFile.Code)
            .Produces<KycCapsDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid customerId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetKycCapsQuery(customerId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            // 404 and never 403: a customer with no live KYC file, one of another tenant and one
            // outside the caller's agency perimeter must all look the same from outside.
            : Results.NotFound(new { error = result.Error });
    }
}
