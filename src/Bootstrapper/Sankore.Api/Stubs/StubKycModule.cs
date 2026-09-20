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
}
