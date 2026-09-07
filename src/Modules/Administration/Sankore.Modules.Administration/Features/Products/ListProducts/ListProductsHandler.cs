using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.ListProducts;

internal sealed class ListProductsHandler(AdministrationDbContext db)
    : IRequestHandler<ListProductsQuery, Result<IReadOnlyList<ProductDto>>>
{
    public async Task<Result<IReadOnlyList<ProductDto>>> Handle(ListProductsQuery request, CancellationToken ct)
    {
        var products = await db.ProductSpecialities
            .OrderBy(p => p.Name)
            .Select(p => new ProductDto(p.Id, p.Name, p.Code, p.Description))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ProductDto>>(products);
    }
}