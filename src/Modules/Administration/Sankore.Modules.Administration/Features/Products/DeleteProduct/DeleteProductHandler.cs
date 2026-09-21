using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.DeleteProduct;

internal sealed class DeleteProductHandler(AdministrationDbContext db)
    : IRequestHandler<DeleteProductCommand, Result>
{
    public async Task<Result> Handle(DeleteProductCommand request, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == request.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND");

        var effectiveTo = request.EffectiveTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var result = product.Retire(effectiveTo);

        if (result.IsFailure)
            return result;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}
