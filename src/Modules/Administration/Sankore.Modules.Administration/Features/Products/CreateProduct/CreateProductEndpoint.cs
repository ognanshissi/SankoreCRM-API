using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal static class CreateProductEndpoint
{
    internal static IEndpointRouteBuilder MapCreateProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost("", Handle)
            .WithName("CreateProduct")
            .WithSummary("Create a product speciality")
            .WithDescription("Creates a new product speciality for the tenant. Code must be unique per tenant. Requires permission: product:create.")
            .RequireAuthorization(Permissions.CanCreateProduct.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateProductRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateProductCommand(req.Name, req.Code, req.Description), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/products/{result.Value}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

internal sealed record CreateProductRequest(string Name, string Code, string? Description);