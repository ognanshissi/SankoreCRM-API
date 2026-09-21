using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Products.LinkProductToCbs;

internal sealed class LinkProductToCbsHandler(AdministrationDbContext db)
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
        return Result.Ok();
    }
}
