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
        var q = db.ProductSpecialities.AsQueryable();

        if (request.ActiveOnly == true)
            q = q.Where(p => p.IsActive);

        var products = await q
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Name)
            .Select(p => new ProductDto(
                p.Id, p.Name, p.Code, p.Category,
                p.Description, p.ParametersJson,
                p.IsActive, p.EffectiveFrom, p.EffectiveTo))
            .ToListAsync(ct);

        return Result.Ok<IReadOnlyList<ProductDto>>(products);
    }
}
