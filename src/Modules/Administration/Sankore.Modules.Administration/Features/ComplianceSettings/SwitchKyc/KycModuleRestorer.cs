namespace Sankore.Modules.Administration.Features.ComplianceSettings.SwitchKyc;

using MediatR;
using Microsoft.Extensions.Logging;
using Sankore.Shared.Kernel;

/// <summary>
/// Puts an institution's KYC switch back ON once a regulated module is active (product owner,
/// 2026-11-10: « activer un module rallume le KYC »).
///
/// <para>
/// While the module is active KYC is in force anyway (<see cref="TenantKycState.InForce"/>); this
/// writes it down so that deactivating the module later does not switch KYC off again behind
/// everyone's back. The registry decides modules and cannot call this host, so
/// <c>NotifyTenantModuleDecisionsJob</c> runs it for every live tenant on each of its passes —
/// whether the module came from a request or from the platform directly.
/// </para>
/// </summary>
internal sealed class KycModuleRestorer(
    ITenantStore store,
    ITenantKyc tenantKyc,
    ISender sender,
    ILogger<KycModuleRestorer> logger)
{
    /// <returns>Whether the switch was put back on. Must run in the tenant's background scope.</returns>
    public async Task<bool> RestoreAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            // A twin follows its production's switch: nothing of its own to restore.
            if ((await store.GetAsync(tenantId, ct))?.ParentTenantId is not null) return false;

            var state = await tenantKyc.GetAsync(tenantId, ct);
            if (state.SwitchedOn || state.RequiredByModule is null) return false;

            var result = await sender.Send(new SwitchKycCommand(true, state.RequiredByModule), ct);
            if (result.IsFailure)
            {
                logger.LogWarning("KYC of tenant {TenantId} not put back on: {Error}", tenantId, result.Error);
                return false;
            }

            logger.LogInformation("KYC of tenant {TenantId} put back on: module {Module} active",
                tenantId, state.RequiredByModule);
            return result.Value.Changed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never the reason a decision e-mail is not sent: KYC is in force through the module anyway.
            logger.LogWarning(ex, "KYC of tenant {TenantId} not checked against its modules", tenantId);
            return false;
        }
    }
}
