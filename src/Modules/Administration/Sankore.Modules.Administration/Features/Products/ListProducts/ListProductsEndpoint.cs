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
            .WithSummary("List financial products for the tenant")
            .RequireAuthorization(Permissions.CanReadProduct.Code)
            .Produces<IReadOnlyList<ProductDto>>(StatusCodes.Status200OK)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListProductsQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }
}
