using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.UpdateProduct;

internal sealed class UpdateProductHandler(AdministrationDbContext db)
    : IRequestHandler<UpdateProductCommand, Result>
{
    public async Task<Result> Handle(UpdateProductCommand request, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND: Product not found.");

        product.Update(request.Name, request.Description);
        await db.SaveChangesAsync(ct);

        return Result.Ok();
    }
}