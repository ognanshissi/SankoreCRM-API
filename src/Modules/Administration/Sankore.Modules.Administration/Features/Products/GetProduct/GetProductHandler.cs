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
        var product = await db.ProductSpecialities
            .Where(p => p.Id == request.ProductId)
            .Select(p => new ProductDto(
                p.Id, p.Name, p.Code, p.Category,
                p.Description, p.ParametersJson,
                p.IsActive, p.EffectiveFrom, p.EffectiveTo,
                p.BusinessProductId, p.BusinessPlatformName))
            .FirstOrDefaultAsync(ct);

        if (product is null)
            return Result.Fail<ProductDto>("PRODUCT_NOT_FOUND");

        return Result.Ok(product);
    }
}
