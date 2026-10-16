namespace Sankore.Modules.Integration.Features.Telephony.Recordings;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// One tenant's recording policy, or the <c>Disabled</c> one (TEL-B-15).
///
/// <para>
/// A reader of its own so no caller has to remember that an absent row means <c>Disabled</c>. The
/// alternative — a nullable policy handed round — is one null check away from a job that reads
/// "no policy" as "no opinion" and keeps a recording nobody authorised.
/// </para>
/// </summary>
internal sealed class TelephonyRecordingPolicyReader(IntegrationDbContext db)
{
    /// <summary>
    /// Takes the tenant explicitly: the fetch and purge jobs run under SYSTEM, where the ambient
    /// context is not the tenant being swept.
    /// </summary>
    internal async Task<TelRecordingPolicy> ReadAsync(Guid tenantId, CancellationToken ct)
        => await db.TelRecordingPolicies
               .IgnoreQueryFilters()
               .FirstOrDefaultAsync(p => p.TenantId == tenantId, ct)
           ?? TelRecordingPolicy.DisabledFor(tenantId);
}
