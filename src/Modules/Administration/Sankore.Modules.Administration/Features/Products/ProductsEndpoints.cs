using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Administration.Features.Products.CreateProduct;
using Sankore.Modules.Administration.Features.Products.DeleteProduct;
using Sankore.Modules.Administration.Features.Products.GetProduct;
using Sankore.Modules.Administration.Features.Products.ListProducts;
using Sankore.Modules.Administration.Features.Products.UpdateProduct;

namespace Sankore.Modules.Administration.Features.Products;

internal static class ProductsEndpoints
{
    internal static IEndpointRouteBuilder MapProductsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("products").WithTags("Products");
        group.MapListProducts();
        group.MapGetProduct();
        group.MapCreateProduct();
        group.MapUpdateProduct();
        group.MapDeleteProduct();
        return app;
    }
}
