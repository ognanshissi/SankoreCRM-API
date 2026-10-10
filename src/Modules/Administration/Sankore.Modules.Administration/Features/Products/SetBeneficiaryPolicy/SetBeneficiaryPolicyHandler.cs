namespace Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetBeneficiaryPolicyHandler(
    AdministrationDbContext db,
    ProductCatalogCache catalogCache)
    : IRequestHandler<SetBeneficiaryPolicyCommand, Result>
{
    public async Task<Result> Handle(SetBeneficiaryPolicyCommand cmd, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == cmd.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND");

        var policy = BeneficiaryPolicy.Create(
            householdEnabled: cmd.HouseholdEnabled,
            allowedRoles: [.. (cmd.AllowedRoles ?? []).Select(r => new DesignationRoleDefinition(
                r.Code, r.Label, r.MinCount, r.MaxCount, r.SharesMustTotal100))],
            maxChildren: cmd.MaxChildren,
            childMaxAge: cmd.ChildMaxAge,
            requiredFieldsBeforeConversion: cmd.RequiredFieldsBeforeConversion,
            requiredKycTier: cmd.RequiredKycTier,
            integrationSystem: cmd.IntegrationSystem);

        // The aggregate is the authority on whether the fields hang together, so a refusal here
        // changes nothing — including AllowMultipleHoldings, which is part of the same decision
        // and must not be applied on its own.
        if (policy.IsFailure)
            return Result.Fail(policy.Error!);

        product.SetBeneficiaryPolicy(policy.Value);
        product.SetAllowMultipleHoldings(cmd.AllowMultipleHoldings);

        await db.SaveChangesAsync(ct);

        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);

        return Result.Ok();
    }
}
