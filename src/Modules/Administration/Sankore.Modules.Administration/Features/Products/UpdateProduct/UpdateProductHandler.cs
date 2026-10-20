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

        if (request.CustomFields is { Count: > 0 })
        {
            var custom = await ProductFormGate.ApplyAsync(
                db, product.CustomFieldsJson, request.CustomFields,
                new Dictionary<string, string?> { ["category"] = product.Category.ToString() },
                isCreate: false, ct);

            if (custom.IsFailure)
                return Result.Fail(custom.Error!);

            product.SetCustomFields(custom.Value);
        }

        // The CBS link has its own endpoint (LinkProductToCbs) and the catalogue screen leaves it out
        // of this PUT on purpose — which used to ERASE it: Update assigned the two absent fields, so
        // saving a product's name silently unlinked it from the core banking system. Absent now
        // means "unchanged"; the link is set and changed where it belongs.
        product.Update(
            request.Name,
            request.Description,
            request.ParametersJson,
            request.BusinessProductId ?? product.BusinessProductId,
            request.BusinessPlatformName ?? product.BusinessPlatformName);

        await db.SaveChangesAsync(ct);

        // Drops this tenant's cached policy and catalogue (BNF-B-06). AFTER SaveChanges on
        // purpose: invalidating first opens a window in which a concurrent read repopulates the
        // cache from the pre-write row, which is the one ordering that makes a cache permanently
        // wrong instead of briefly stale.
        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);

        return Result.Ok();
    }
}
