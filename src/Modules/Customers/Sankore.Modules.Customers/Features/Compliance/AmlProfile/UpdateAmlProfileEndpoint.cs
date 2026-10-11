namespace Sankore.Modules.Customers.Features.Compliance.AmlProfile;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

internal static class UpdateAmlProfileEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateAmlProfile(this IEndpointRouteBuilder app)
    {
        app.MapPut("{clientId:guid}/aml-profile", Handle)
            .WithName("UpdateClientAmlProfile")
            .WithSummary("Record a client's LBC/FT profile")
            .WithDescription(
                "The sector of activity (code from the same referential the core-banking mapping "
                + "uses, plus what the institution established in words), the origin of the funds "
                + "(a code from a closed list plus an ENCRYPTED narrative), and for a legal person "
                + "the stated object and the issue date of the registry extract on file. "
                + "A null field is left unchanged and a blank one clears it. "
                + "The CIMA LBC/FT text is explicit that a profession alone is not enough — "
                + "« commerçant » does not describe a sector. Audited; the funds-origin narrative "
                + "is recorded by field name only, and no endpoint returns it. "
                + "Requires permission: customers:update.")
            .RequireAuthorization(Permissions.CanUpdateCustomer.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid clientId, UpdateAmlProfileRequest body, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateAmlProfileCommand(
            ClientId: clientId,
            ExpectedVersion: body.ExpectedVersion,
            ActivitySectorCode: body.ActivitySectorCode,
            ActivitySectorDetail: body.ActivitySectorDetail,
            FundsOriginCode: body.FundsOriginCode,
            FundsOriginDetail: body.FundsOriginDetail,
            CorporatePurpose: body.CorporatePurpose,
            RegistrationExtractIssuedOn: body.RegistrationExtractIssuedOn), ct);

        if (result.IsSuccess) return Results.NoContent();

        return result.Error switch
        {
            CustomerErrors.ClientNotFound => Results.NotFound(new { error = result.Error }),
            CustomerErrors.ConcurrencyConflict or CustomerErrors.ClientReadOnly
                => Results.Conflict(new { error = result.Error }),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    }
}
