using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.UpdateProduct;

internal sealed class UpdateProductHandler(
    AdministrationDbContext db, ProductCatalogCache catalogCache)
    : IRequestHandler<UpdateProductCommand, Result>
{
    public async Task<Result> Handle(UpdateProductCommand request, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND");

        product.Update(
            request.Name,
            request.Description,
            request.ParametersJson,
            request.BusinessProductId,
            request.BusinessPlatformName);

        await db.SaveChangesAsync(ct);

        // Drops this tenant's cached policy and catalogue (BNF-B-06). AFTER SaveChanges on
        // purpose: invalidating first opens a window in which a concurrent read repopulates the
        // cache from the pre-write row, which is the one ordering that makes a cache permanently
        // wrong instead of briefly stale.
        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);

        return Result.Ok();
    }
}
