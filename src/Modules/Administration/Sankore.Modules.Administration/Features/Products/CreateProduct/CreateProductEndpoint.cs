using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal static class CreateProductEndpoint
{
    internal static IEndpointRouteBuilder MapCreateProduct(this IEndpointRouteBuilder app)
    {
        app.MapPost("", Handle)
            .WithName("CreateProduct")
            .WithSummary("Create a financial product")
            .RequireAuthorization(Permissions.CanCreateProduct.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        CreateProductRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateProductCommand(
            req.Name, req.Code, req.Category,
            req.Description, req.ParametersJson, req.EffectiveFrom), ct);

        return result.IsSuccess
            ? Results.Created($"/api/v1/products/{result.Value}", result.Value)
            : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

internal sealed record CreateProductRequest(
    string Name,
    string Code,
    ProductCategory Category,
    string? Description = null,
    string? ParametersJson = null,
    DateOnly? EffectiveFrom = null);
