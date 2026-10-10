using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal sealed class CreateProductHandler(
    AdministrationDbContext db,
    ITenantContext tenant,
    ProductCatalogCache catalogCache
) : IRequestHandler<CreateProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken ct)
    {
        var codeNormalized = request.Code.ToUpperInvariant();
        var exists = await db.ProductSpecialities
            .AnyAsync(p => p.Code == codeNormalized, ct);

        if (exists)
            return Result.Fail<Guid>("PRODUCT_CODE_TAKEN");

        var product = ProductSpeciality.Create(
            tenant.CurrentTenantId,
            request.Name,
            request.Code,
            request.Category,
            request.Description,
            request.ParametersJson,
            request.EffectiveFrom);

        await db.ProductSpecialities.AddAsync(product, ct);
        await db.SaveChangesAsync(ct);

        // Drops this tenant's cached policy and catalogue (BNF-B-06). AFTER SaveChanges on
        // purpose: invalidating first opens a window in which a concurrent read repopulates the
        // cache from the pre-write row, which is the one ordering that makes a cache permanently
        // wrong instead of briefly stale.
        await catalogCache.InvalidateAsync(tenant.CurrentTenantId, product.Id, ct);

        return Result.Ok(product.Id);
    }
}
