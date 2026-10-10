namespace Sankore.Modules.Integration.Features.Insurance.UpdateInsuranceProduct;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class UpdateInsuranceProductHandler(
    IntegrationDbContext db,
    LinkedCreditProductCheck creditProducts,
    CrmProductCatalogue crmProducts,
    InsurerPricingProbe pricing,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<UpdateInsuranceProductCommand, Result<InsuranceProductDto>>
{
    public async Task<Result<InsuranceProductDto>> Handle(
        UpdateInsuranceProductCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // AsTracking: this one is loaded to be mutated. No tenant predicate — the query filter
        // scopes it, so another tenant's product answers 404 and never 403.
        var found = await db.FindWithConnectionAsync(cmd.ProductId, ct, tracking: true);

        if (found is null)
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductNotFound);

        var (product, connection) = found.Value;

        var linked = await creditProducts.VerifyAsync(cmd.Body.LinkedCreditProductCode, ct);
        if (linked.IsFailure) return Result.Fail<InsuranceProductDto>(linked.Error!);

        var catalogued = await crmProducts.VerifyAsync(cmd.Body.CrmProductCode, ct);
        if (catalogued.IsFailure) return Result.Fail<InsuranceProductDto>(catalogued.Error!);

        var body = cmd.Body;

        try
        {
            product.Update(
                name: body.Name,
                description: body.Description,
                guaranteesJson: InsuranceGuaranteeCodec.Serialise(body.Guarantees),
                periodicity: body.Periodicity,
                pricingMode: body.PricingMode,
                fixedPremiumAmount: body.FixedPremiumAmount,
                currency: body.Currency,
                insuredAmount: body.InsuredAmount,
                minAge: body.MinAge,
                maxAge: body.MaxAge,
                minKycLevel: body.MinKycLevel,
                requiresCbsAccount: body.RequiresCbsAccount,
                requiresActiveLoan: body.RequiresActiveLoan,
                linkedCreditProductCode: body.LinkedCreditProductCode,
                crmProductCode: body.CrmProductCode,
                commissionRate: body.CommissionRate,
                premiumRetryLimit: body.PremiumRetryLimit,
                premiumRetryIntervalDays: body.PremiumRetryIntervalDays,
                effectiveFrom: body.EffectiveFrom,
                effectiveTo: body.EffectiveTo,
                updatedBy: currentUser.Id,
                clock: clock);
        }
        catch (DomainException ex)
        {
            return Result.Fail<InsuranceProductDto>($"{IntegrationErrors.SettingsInvalid}: {ex.Message}");
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin. Two administrators editing one product: the second is told rather than
            // silently overwriting a commission rate the first just changed.
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.ConcurrencyConflict);
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var crmCatalogue = await crmProducts.StatusAsync(product.CrmProductCode, ct);

        return Result.Ok(InsuranceProductDto.From(
            product, connection, pricing.CanPrice(connection), crmCatalogue, today));
    }
}
