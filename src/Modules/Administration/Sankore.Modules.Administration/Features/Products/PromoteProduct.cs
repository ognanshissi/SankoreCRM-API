namespace Sankore.Modules.Administration.Features.Products;

using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Administration.Features.Products.CreateProduct;
using Sankore.Modules.Administration.Features.Products.SetBeneficiaryPolicy;
using Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;
using Sankore.Modules.Administration.Features.Products.UpdateProduct;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.TestMode;
using Sankore.Shared.Kernel;

/// <summary>
/// « Copier en production » for a product (sandbox lot 4) — the copy itself is
/// <see cref="ProductCopier"/>, which the refresh of a test mode (lot 5) runs the other way.
/// </summary>
// Audited like any save — in the test mode, by whoever copied; the replayed commands are audited
// again in production. ProductionReplay suppresses this command's transaction for the replay.
internal sealed record PromoteProductCommand(Guid ProductId, bool DryRun)
    : IRequest<Result<PromotionResult>>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductSpeciality";
    public string? ResourceId => ProductId.ToString();
}

internal sealed class PromoteProductHandler(ProductionReplay production, ProductCopier copier)
    : IRequestHandler<PromoteProductCommand, Result<PromotionResult>>
{
    public Task<Result<PromotionResult>> Handle(PromoteProductCommand cmd, CancellationToken ct)
        => production.InTestMode
            ? copier.CopyAsync(production.ToProduction(cmd.ProductId, cmd.DryRun), ct)
            : Task.FromResult(Result.Fail<PromotionResult>(PromotionErrors.TestModeRequired));
}

/// <summary>
/// One product from one tenant to another. The target product is the one with the same CODE — the
/// key everything else names a product by (project templates, questionnaires, projects) — created
/// when absent. Its fields, custom values, beneficiary rules and remote-KYC policy are replayed
/// through the ordinary commands, so the target's own checks still apply (an « automatic » KYC
/// policy on a tenant that forbids it is refused there as it would be in the editor). The
/// back-office (CBS) link is each tenant's own and is never touched.
/// </summary>
internal sealed class ProductCopier(AdministrationDbContext db, IServiceScopeFactory scopes) : IConfigurationCopier
{
    public string Kind => "product";

    public int Order => 10;

    public async Task<IReadOnlyList<Guid>> ListAsync(Guid tenantId, CancellationToken ct)
        => await db.ProductSpecialities.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).Select(p => p.Id).ToListAsync(ct);

    public async Task<Result<PromotionResult>> CopyAsync(ConfigurationCopy copy, CancellationToken ct)
    {
        var source = await db.ProductSpecialities.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == copy.SourceId && p.TenantId == copy.SourceTenantId, ct);
        if (source is null) return Result.Fail<PromotionResult>(PromotionErrors.SourceNotFound);

        var target = await db.ProductSpecialities.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == copy.TargetTenantId && p.Code == source.Code, ct);

        var action = target is null ? PromotionActions.Created : PromotionActions.Updated;
        List<string> notes = [];
        if (source.BusinessProductId is not null || target?.BusinessProductId is not null)
            notes.Add(copy.ToProduction
                ? "La liaison au core banking n’est pas copiée : la production garde la sienne."
                : "La liaison au core banking n’est pas copiée : le mode test garde la sienne.");

        if (copy.DryRun) return Result.Ok(new PromotionResult(action, target?.Id, [], notes));

        var customFields = string.IsNullOrWhiteSpace(source.CustomFieldsJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.CustomFieldsJson);

        var copied = await TenantReplay.RunAsync<Result<Guid>>(scopes, copy.TargetTenantId, copy.UserId, copy.UserName, async (sender, _) =>
        {
            Guid id;
            if (target is null)
            {
                var created = await sender.Send(new CreateProductCommand(
                    source.Name, source.Code, source.Category, source.Description, source.ParametersJson, source.EffectiveFrom, customFields), ct);
                if (created.IsFailure) return created;
                id = created.Value;
            }
            else
            {
                var updated = await sender.Send(new UpdateProductCommand(
                    target.Id, source.Name, source.Description, source.ParametersJson,
                    target.BusinessProductId, target.BusinessPlatformName, customFields), ct);
                if (updated.IsFailure) return Result.Fail<Guid>(updated.Error!);
                id = target.Id;
            }

            var beneficiaries = source.BeneficiaryPolicy;
            var rules = await sender.Send(new SetBeneficiaryPolicyCommand(
                id, source.AllowMultipleHoldings,
                beneficiaries.AllowedRoles?.Select(r => new DesignationRoleInput(r.Code, r.Label, r.MinCount, r.MaxCount, r.SharesMustTotal100)).ToList(),
                beneficiaries.MaxChildren, beneficiaries.ChildMaxAge, beneficiaries.RequiredFieldsBeforeConversion,
                beneficiaries.RequiredKycTier, beneficiaries.IntegrationSystem), ct);
            if (rules.IsFailure) return Result.Fail<Guid>(rules.Error!);

            var kyc = source.KycRemotePolicy;
            var remote = await sender.Send(new SetKycRemotePolicyCommand(
                id, kyc.Enabled, kyc.ValidationMode, kyc.RequiredTier, kyc.MinAutoScore,
                kyc.RequiredDocuments, kyc.AllowedDocumentTypes, kyc.LinkValidityHours), ct);
            return remote.IsFailure ? Result.Fail<Guid>(remote.Error!) : Result.Ok(id);
        });

        return copied.IsFailure
            ? Result.Fail<PromotionResult>(copied.Error!)
            : Result.Ok(new PromotionResult(action, copied.Value, [], notes));
    }
}

internal static class PromoteProductEndpoint
{
    public static IEndpointRouteBuilder MapPromoteProduct(this IEndpointRouteBuilder group)
    {
        group.MapPromote("Product", Permissions.CanUpdateProduct.Code,
            (id, dryRun, sender, ct) => sender.Send(new PromoteProductCommand(id, dryRun), ct));
        return group;
    }
}
