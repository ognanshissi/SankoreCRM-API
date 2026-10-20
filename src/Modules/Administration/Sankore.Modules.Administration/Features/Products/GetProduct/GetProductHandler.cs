using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.GetProduct;

internal sealed class GetProductHandler(AdministrationDbContext db)
    : IRequestHandler<GetProductQuery, Result<ProductDto>>
{
    public async Task<Result<ProductDto>> Handle(GetProductQuery request, CancellationToken ct)
    {
        // Loaded whole and mapped in memory: the custom fields are parsed from their jsonb text,
        // which a SQL projection cannot do.
        var product = await db.ProductSpecialities
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);

        if (product is null)
            return Result.Fail<ProductDto>("PRODUCT_NOT_FOUND");

        return Result.Ok(ProductFormGate.ToDto(product));
    }
}
