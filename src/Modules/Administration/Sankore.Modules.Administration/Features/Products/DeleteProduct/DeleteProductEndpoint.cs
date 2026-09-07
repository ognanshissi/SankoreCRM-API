using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.DeleteProduct;

internal static class DeleteProductEndpoint
{
    internal static IEndpointRouteBuilder MapDeleteProduct(this IEndpointRouteBuilder app)
    {
        app.MapDelete("{id:guid}", Handle)
            .WithName("DeleteProduct")
            .WithSummary("Delete a product speciality")
            .RequireAuthorization(Permissions.CanDeleteProduct.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeleteProductCommand(id), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : Results.NotFound(new { error = result.Error });
    }
}
