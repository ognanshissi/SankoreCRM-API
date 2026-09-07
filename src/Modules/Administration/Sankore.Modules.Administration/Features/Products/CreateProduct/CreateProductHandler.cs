using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.CreateProduct;

internal sealed class CreateProductHandler(
    AdministrationDbContext db,
    ITenantContext tenant
) : IRequestHandler<CreateProductCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken ct)
    {
        var codeNormalized = request.Code.ToUpperInvariant();
        var exists = await db.ProductSpecialities
            .AnyAsync(p => p.Code == codeNormalized, ct);

        if (exists)
            return Result.Fail<Guid>($"PRODUCT_CODE_TAKEN: A product with code '{codeNormalized}' already exists.");

        var product = ProductSpeciality.Create(
            tenant.CurrentTenantId,
            request.Name,
            request.Code,
            request.Description);

        await db.ProductSpecialities.AddAsync(product, ct);
        await db.SaveChangesAsync(ct);

        return Result.Ok(product.Id);
    }
}