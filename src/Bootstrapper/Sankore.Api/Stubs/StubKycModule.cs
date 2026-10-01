namespace Sankore.Api.Stubs;

using Sankore.Modules.Kyc.PublicApi;

/// <summary>
/// Temporary stub — returns NotStarted for all queries until the
/// KYC module (M02) is scaffolded and registered.
/// </summary>
internal sealed class StubKycModule : IKycModule
{
    public Task<KycStatus?> GetStatusAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct)
        => Task.FromResult<KycStatus?>(KycStatus.NotStarted);

    /// <summary>
    /// Fail-closed: with no KYC module deployed, nothing can attest that the
    /// compliance retention obligation is over, so anonymization stays blocked
    /// with <c>KYC_RETENTION_NOT_CLEARED</c>.
    /// </summary>
    public Task<bool> IsRetentionClearedAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>Fail-closed as well: no file, no permission to operate.</summary>
    public Task<KycLimits?> GetLimitsAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct)
        => Task.FromResult<KycLimits?>(null);

    public Task<KycFlowUsage> GetFlowUsageAsync(Guid tenantId, Guid customerEntityId, CancellationToken ct)
        => Task.FromResult(KycFlowUsage.Unknown);
}
