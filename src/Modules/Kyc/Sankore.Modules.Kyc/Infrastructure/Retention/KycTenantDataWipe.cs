namespace Sankore.Modules.Kyc.Infrastructure.Retention;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Kyc.Domain;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>The KYC files of a TEST institution and their documents, emptied (data-purge lot 4).</summary>
internal sealed class KycTenantDataWipe(KycDbContext db, IServiceProvider services, ILogger<KycTenantDataWipe> logger) : ITenantDataWipe
{
    internal static readonly Type[] Keep = [typeof(KycSetting)];

    public string Module => "Kyc";

    public async Task<int> WipeAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);
        await TenantObjects.WipeAsync(services, tenantId, [KycObjectStorage.Concern], logger, ct);
        return rows;
    }
}
