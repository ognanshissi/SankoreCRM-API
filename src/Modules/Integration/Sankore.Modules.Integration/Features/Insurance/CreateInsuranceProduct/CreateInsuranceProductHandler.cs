namespace Sankore.Modules.Integration.Features.Insurance.CreateInsuranceProduct;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Kernel;

internal sealed class CreateInsuranceProductHandler(
    IntegrationDbContext db,
    LinkedCreditProductCheck creditProducts,
    CrmProductCatalogue crmProducts,
    InsurerPricingProbe pricing,
    ITenantContext tenant,
    ICurrentUser currentUser,
    TimeProvider clock)
    : IRequestHandler<CreateInsuranceProductCommand, Result<InsuranceProductDto>>
{
    public async Task<Result<InsuranceProductDto>> Handle(
        CreateInsuranceProductCommand cmd, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        // An unknown connection and a core-banking one answer the same two codes they mean: 404
        // for "no such insurer of yours", 422 for "that connection is a CBS". Both before any
        // write, because a catalogue entry on a core-banking connection would resolve a CBS
        // adapter when ASS-04 came to subscribe it — at the counter, not here.
        var connection = await db.FindInsuranceConnectionAsync(cmd.ConnectionId, ct);

        if (connection is null)
        {
            var exists = await db.Connections.AnyAsync(c => c.Id == cmd.ConnectionId, ct);

            return Result.Fail<InsuranceProductDto>(exists
                ? IntegrationErrors.InsuranceConnectionWrongFamily
                : IntegrationErrors.ConnectionNotFound);
        }

        var linked = await creditProducts.VerifyAsync(cmd.Body.LinkedCreditProductCode, ct);
        if (linked.IsFailure) return Result.Fail<InsuranceProductDto>(linked.Error!);

        // The CRM catalogue entry this agreement realises. Checked here and not only rendered,
        // because an unverified code is a link that dangles from the moment it is written — and
        // the field it would silently break is the one every product-by-code report keys on.
        var catalogued = await crmProducts.VerifyAsync(cmd.Body.CrmProductCode, ct);
        if (catalogued.IsFailure) return Result.Fail<InsuranceProductDto>(catalogued.Error!);

        var code = cmd.InsurerProductCode.Trim();

        // The friendly half of ux_ins_product_insurer_code. The index is the guarantee — two
        // administrators configuring the same product at once both pass this — and its violation
        // is caught below so the race answers the same code as the ordinary case.
        var alreadyThere = await db.InsuranceProducts
            .AnyAsync(p => p.ConnectionId == cmd.ConnectionId && p.InsurerProductCode == code, ct);

        if (alreadyThere)
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductAlreadyExists);

        InsuranceProduct product;

        try
        {
            product = Build(cmd, tenant.CurrentTenantId, code, currentUser.Id, clock);
        }
        catch (DomainException ex)
        {
            // The aggregate's own invariants — a catalogue-priced product with no price, a
            // commission rate outside [0,1], an inverted validity window. The validator refuses
            // all of them first; this is the net for a caller that is not an HTTP request.
            return Result.Fail<InsuranceProductDto>($"{IntegrationErrors.SettingsInvalid}: {ex.Message}");
        }

        db.InsuranceProducts.Add(product);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateProduct(ex))
        {
            db.ChangeTracker.Clear();
            return Result.Fail<InsuranceProductDto>(IntegrationErrors.InsuranceProductAlreadyExists);
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // Already resolved and cached by the VerifyAsync above: no second call to M12.
        var crmCatalogue = await crmProducts.StatusAsync(product.CrmProductCode, ct);

        return Result.Ok(InsuranceProductDto.From(
            product, connection, pricing.CanPrice(connection), crmCatalogue, today));
    }

    private static InsuranceProduct Build(
        CreateInsuranceProductCommand cmd, Guid tenantId, string code, Guid actor, TimeProvider clock)
    {
        var body = cmd.Body;

        return InsuranceProduct.Create(
            tenantId: tenantId,
            connectionId: cmd.ConnectionId,
            insurerProductCode: code,
            name: body.Name,
            periodicity: body.Periodicity,
            pricingMode: body.PricingMode,
            createdBy: actor,
            clock: clock,
            effectiveFrom: body.EffectiveFrom,
            description: body.Description,
            guaranteesJson: InsuranceGuaranteeCodec.Serialise(body.Guarantees),
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
            effectiveTo: body.EffectiveTo);
    }

    /// <summary>
    /// Matched by index NAME, like every other unique-violation guard in this module: any other
    /// constraint failing is a bug and must keep propagating instead of being reported as "that
    /// product already exists".
    /// </summary>
    private static bool IsDuplicateProduct(DbUpdateException ex)
        => ex.InnerException is Npgsql.PostgresException
           {
               SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
           } pg
           && pg.ConstraintName?.Contains("ux_ins_product_insurer_code", StringComparison.OrdinalIgnoreCase) == true;
}
