using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.GetProduct;

internal static class GetProductEndpoint
{
    internal static IEndpointRouteBuilder MapGetProduct(this IEndpointRouteBuilder app)
    {
        app.MapGet("{id:guid}", Handle)
            .WithName("GetProduct")
            .WithSummary("Get a product speciality by ID")
            .RequireAuthorization(Permissions.CanReadProduct.Code)
            .Produces<ProductDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(Guid id, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetProductQuery(id), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }
}
