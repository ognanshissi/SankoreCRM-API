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
            .WithSummary("Update a product speciality")
            .WithDescription("Updates name and description. Code is immutable after creation. Requires permission: product:update.")
            .RequireAuthorization(Permissions.CanUpdateProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
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
        var result = await sender.Send(new UpdateProductCommand(id, req.Name, req.Description), ct);

        if (!result.IsSuccess)
        {
            var isNotFound = result.Error!.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase);
            return isNotFound
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }
}

internal sealed record UpdateProductRequest(string Name, string? Description);
