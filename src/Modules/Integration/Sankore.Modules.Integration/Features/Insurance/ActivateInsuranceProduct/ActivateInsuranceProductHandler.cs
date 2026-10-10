namespace Sankore.Modules.Integration.Features.Insurance.ActivateInsuranceProduct;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class ActivateInsuranceProductHandler(
    IntegrationDbContext db,
    InsurerPricingProbe pricing,
    CrmProductCatalogue crmProducts,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<ActivateInsuranceProductCommand, Result<InsuranceProductDto>>
{
    public async Task<Result<InsuranceProductDto>> Handle(
        ActivateInsuranceProductCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        var found = await db.FindWithConnectionAsync(cmd.ProductId, ct, tracking: true);

        if (found is null)
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductNotFound);

        var (product, connection) = found.Value;

        // Idempotent: an already-active product is a success and not a conflict, the same call
        // ActivateConnectionHandler makes — a double-click on the button must not look like a rule
        // firing.
        if (!product.IsActive)
        {
            product.Activate(currentUser.Id, clock);

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

        // The verdict comes back with the row, so a screen that activated a product whose
        // connection is down is told immediately instead of discovering it at the counter.
        var crmCatalogue = await crmProducts.StatusAsync(product.CrmProductCode, ct);

        return Result.Ok(InsuranceProductDto.From(
            product, connection, pricing.CanPrice(connection), crmCatalogue, today));
    }
}
