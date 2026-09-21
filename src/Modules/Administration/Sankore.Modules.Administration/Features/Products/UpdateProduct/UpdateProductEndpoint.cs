using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.UpdateProduct;

internal static class UpdateProductEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateProduct(this IEndpointRouteBuilder app)
    {
        app.MapPut("{id:guid}", Handle)
            .WithName("UpdateProduct")
            .WithSummary("Update a financial product")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid id,
        UpdateProductRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateProductCommand(
            id, req.Name, req.Description, req.ParametersJson,
            req.BusinessProductId, req.BusinessPlatformName), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error!.Contains("NOT_FOUND")
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
    }
}

internal sealed record UpdateProductRequest(
    string Name,
    string? Description = null,
    string? ParametersJson = null,
    string? BusinessProductId = null,
    string? BusinessPlatformName = null);
