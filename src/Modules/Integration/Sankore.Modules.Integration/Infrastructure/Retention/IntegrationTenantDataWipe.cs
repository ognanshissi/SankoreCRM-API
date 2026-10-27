namespace Sankore.Modules.Integration.Infrastructure.Retention;

using Microsoft.Extensions.Logging;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.BatchStorage;
using Sankore.Modules.Integration.Infrastructure.ClaimStorage;
using Sankore.Modules.Integration.Infrastructure.RecordingStorage;
using Sankore.Shared.Infrastructure.Retention;

/// <summary>
/// The integration schema of a TEST institution, emptied (data-purge lot 4): calls and
/// recordings, commands, references, batch files, insurance policies and claims, agenda copies…
/// Its connections, mappings, number links, products and their information sheets stay (<see cref="Keep"/>).
/// </summary>
internal sealed class IntegrationTenantDataWipe(
    IntegrationDbContext db, IServiceProvider services, ILogger<IntegrationTenantDataWipe> logger) : ITenantDataWipe
{
    internal static readonly Type[] Keep =
    [
        typeof(IntegrationConnection), typeof(IntegrationMapping), typeof(IntegrationRelayAgent),
        typeof(TelUserLink), typeof(TelCallOutcome), typeof(TelRecordingPolicy), typeof(InsuranceProduct), typeof(ProductInformationSheet),
        typeof(ConnectedAccount), typeof(ConnectedCalendar), typeof(MailServerSettings), typeof(MailTemplate),
    ];

    public string Module => "Integration";

    public async Task<int> WipeAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await TenantRows.WipeAsync(db, tenantId, Keep, logger, ct);
        await TenantObjects.WipeAsync(services, tenantId,
            [RecordingStorage.Concern, ClaimDocumentStorage.Concern, IntegrationBatchStorage.Concern], logger, ct);
        return rows;
    }
}
