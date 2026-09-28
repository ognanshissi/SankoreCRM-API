namespace Sankore.Modules.Customers.Features.LegalEntities.CreateLegalForm;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

public static class CreateLegalFormEndpoint
{
    public static IEndpointRouteBuilder MapCreateLegalForm(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("CreateLegalForm")
            .WithSummary("Add a legal form to the tenant's closed list")
            .WithDescription(
                "Creates a legal form usable by POST clients/legal. Idempotent: posting a " +
                "code that already exists answers 200 with the existing row instead of " +
                "creating a duplicate. Requires permission: customers:update_sensitive.")
            .RequireAuthorization(Permissions.CanUpdateCustomerSensitive.Code)
            .Produces<CreateLegalFormResult>(StatusCodes.Status201Created)
            .Produces<CreateLegalFormResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateLegalFormRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateLegalFormCommand(
            Code: req.Code,
            Label: req.Label,
            DisplayOrder: req.DisplayOrder ?? 0), ct);

        if (result.IsFailure)
            return Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);

        return result.Value.Created
            ? Results.Created($"/api/v1/legal-forms/{result.Value.Code}", result.Value)
            : Results.Ok(result.Value);
    }
}

public sealed record CreateLegalFormRequest(string Code, string Label, int? DisplayOrder);
