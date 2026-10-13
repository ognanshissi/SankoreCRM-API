namespace Sankore.Modules.Administration.Features.Products.SetKycRemotePolicy;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

internal sealed class SetKycRemotePolicyHandler(
    AdministrationDbContext db,
    ProductCatalogCache catalogCache)
    : IRequestHandler<SetKycRemotePolicyCommand, Result>
{
    public async Task<Result> Handle(SetKycRemotePolicyCommand cmd, CancellationToken ct)
    {
        var product = await db.ProductSpecialities
            .AsTracking()
            .FirstOrDefaultAsync(p => p.Id == cmd.ProductId, ct);

        if (product is null)
            return Result.Fail("PRODUCT_NOT_FOUND");

        // The tenant's ceiling, read from the SAME row M02 will read when it decides whether to
        // approve. A tenant that has never opened the compliance screen has no row, and the
        // platform defaults are its configuration — machine approval off. Reading it here is what
        // makes "Automatic" unsavable on such a tenant instead of savable and inert.
        var compliance = await db.ComplianceSettings
            .Where(s => s.TenantId == product.TenantId)
            .Select(s => new { s.KycAutoApprovalEnabled, s.KycAutoApprovalMaxTier })
            .FirstOrDefaultAsync(ct);

        var autoEnabled = compliance?.KycAutoApprovalEnabled ?? false;
        var maxTier = compliance?.KycAutoApprovalMaxTier ?? KycRemotePolicyValues.SimplifiedTier;

        var policy = KycRemotePolicy.Create(
            enabled: cmd.Enabled,
            validationMode: cmd.ValidationMode,
            requiredTier: cmd.RequiredTier,
            minAutoScore: cmd.MinAutoScore,
            requiredDocuments: cmd.RequiredDocuments,
            allowedDocumentTypes: cmd.AllowedDocumentTypes,
            linkValidityHours: cmd.LinkValidityHours,
            autoApprovalEnabledForTenant: autoEnabled,
            autoApprovalMaxTier: maxTier);

        // The value object is the authority on whether the fields hang together, so a refusal here
        // changes nothing at all — not even the enabled flag, which is part of the same decision.
        if (policy.IsFailure)
            return Result.Fail(policy.Error!);

        product.SetKycRemotePolicy(policy.Value);

        await db.SaveChangesAsync(ct);

        // AFTER SaveChanges, like every other product write: invalidating first opens a window in
        // which a concurrent read repopulates the cache from the pre-write row, and that is the one
        // ordering that makes a cache permanently wrong rather than briefly stale.
        await catalogCache.InvalidateAsync(product.TenantId, product.Id, ct);

        return Result.Ok();
    }
}
