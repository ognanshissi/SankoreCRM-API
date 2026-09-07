using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.ListProducts;

internal static class ListProductsEndpoint
{
    internal static IEndpointRouteBuilder MapListProducts(this IEndpointRouteBuilder app)
    {
        app.MapGet("", Handle)
            .WithName("ListProducts")
            .WithSummary("List all product specialities for the tenant")
            .RequireAuthorization(Permissions.CanReadProduct.Code)
            .Produces<IReadOnlyList<ProductDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ListProductsQuery(), ct);
        return Results.Ok(result.Value);
    }
}
