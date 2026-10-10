namespace Sankore.Modules.Integration.Features.Insurance.DeactivateInsuranceProduct;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class DeactivateInsuranceProductHandler(
    IntegrationDbContext db,
    InsurerPricingProbe pricing,
    CrmProductCatalogue crmProducts,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<DeactivateInsuranceProductCommand, Result<InsuranceProductDto>>
{
    public async Task<Result<InsuranceProductDto>> Handle(
        DeactivateInsuranceProductCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var found = await db.FindWithConnectionAsync(cmd.ProductId, ct, tracking: true);

        if (found is null)
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductNotFound);

        var (product, connection) = found.Value;

        if (product.IsActive)
        {
            product.Deactivate(currentUser.Id, clock);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Result.Fail<InsuranceProductDto>(IntegrationErrors.ConcurrencyConflict);
            }
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var crmCatalogue = await crmProducts.StatusAsync(product.CrmProductCode, ct);

        return Result.Ok(InsuranceProductDto.From(
            product, connection, pricing.CanPrice(connection), crmCatalogue, today));
    }
}
