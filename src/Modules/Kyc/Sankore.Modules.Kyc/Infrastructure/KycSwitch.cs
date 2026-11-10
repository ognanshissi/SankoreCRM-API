namespace Sankore.Modules.Kyc.Infrastructure;

using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Kernel;

/// <summary>
/// For M02's jobs and consumers: whether the institution's KYC is switched off
/// (docs/kyc-par-institution.md), in which case they have nothing to run — no review, no link sent,
/// no reminder. Without an <see cref="ITenantKyc"/> registered (the module's own tests), KYC is in force.
/// </summary>
internal static class KycSwitch
{
    public static async Task<bool> IsOffAsync(IServiceProvider services, Guid tenantId, CancellationToken ct)
        => services.GetService<ITenantKyc>() is { } kyc && !await kyc.IsInForceAsync(tenantId, ct);
}
