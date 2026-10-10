using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.LinkProductToCbs;

internal sealed class LinkProductToCbsHandler(
    AdministrationDbContext db, ProductCatalogCache catalogCache)
    : IRequestHandler<LinkProductToCbsCommand, Result>
{
    public async Task<Result> Handle(LinkProductToCbsCommand cmd, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == cmd.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND");

        // Preserve existing fields; only update CBS link
        product.LinkToCbs(
            cmd.BusinessPlatformName,
            cmd.BusinessProductId);

        db.ProductSpecialities.Update(product);
        await db.SaveChangesAsync(ct);

        // Drops this tenant's cached policy and catalogue (BNF-B-06). AFTER SaveChanges on
        // purpose: invalidating first opens a window in which a concurrent read repopulates the
        // cache from the pre-write row, which is the one ordering that makes a cache permanently
        // wrong instead of briefly stale.
        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);

        return Result.Ok();
    }
}
