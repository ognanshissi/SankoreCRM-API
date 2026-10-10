namespace Sankore.Modules.Integration.Features.Insurance.GetInsuranceProduct;

using MediatR;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

internal sealed class GetInsuranceProductHandler(
    IntegrationDbContext db,
    InsurerPricingProbe pricing,
    CrmProductCatalogue crmProducts,
    TimeProvider clock)
    : IRequestHandler<GetInsuranceProductQuery, Result<InsuranceProductDto>>
{
    public async Task<Result<InsuranceProductDto>> Handle(
        GetInsuranceProductQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        // No tenant predicate: the global query filter scopes it, so another tenant's product is
        // simply absent and answers 404 — never 403, which would confirm the id names a real
        // product and therefore that another institution distributes insurance on this platform.
        var found = await db.FindWithConnectionAsync(query.ProductId, ct);

        if (found is null)
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductNotFound);

        var (product, connection) = found.Value;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var crmCatalogue = await crmProducts.StatusAsync(product.CrmProductCode, ct);

        return Result.Ok(InsuranceProductDto.From(
            product, connection, pricing.CanPrice(connection), crmCatalogue, today));
    }
}
