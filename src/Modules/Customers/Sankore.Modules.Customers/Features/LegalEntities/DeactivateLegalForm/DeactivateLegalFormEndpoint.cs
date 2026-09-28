namespace Sankore.Modules.Customers.Features.LegalEntities.DeactivateLegalForm;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class DeactivateLegalFormEndpoint
{
    public static IEndpointRouteBuilder MapDeactivateLegalForm(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{code}", Handle)
            .WithName("DeactivateLegalForm")
            .WithSummary("Retire a legal form from the tenant's list")
            .WithDescription(
                "Deactivates the legal form — it is never physically deleted, so clients " +
                "already carrying the code keep a readable legal form. An unknown or " +
                "already-retired code answers 404 LEGAL_FORM_UNKNOWN. " +
                "Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        string code,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateLegalFormCommand(code), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error == CustomerErrors.LegalFormUnknown
            ? Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}
