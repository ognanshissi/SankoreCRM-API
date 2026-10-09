namespace Sankore.Modules.Integration.Features.References.GetReference;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Infrastructure;

/// <summary>
/// INT-07's read: the identifier one external system gave a CRM entity, or <c>null</c>.
///
/// <para>
/// <c>null</c> and never an exception. "This customer is not known to that CBS yet" is the
/// ordinary state of every customer the day before their creation command runs, and of every
/// customer of a tenant that has just connected a second insurer. A lookup that threw would make
/// every caller wrap it, and one of them would wrap it wrongly.
/// </para>
///
/// <para>
/// A small class rather than a MediatR query because its callers are not endpoints: the
/// dispatcher needs it on every operation that touches an existing entity, the reconciliation
/// walks it, and the facade reads it per connection. Going through the pipeline for that would
/// buy a transaction and an audit row per lookup.
/// </para>
///
/// <para>
/// Per CONNECTION, always. The same customer legitimately holds one identifier per external
/// system, and a lookup that forgot the connection would answer with whichever insurer's number
/// happened to be written first.
/// </para>
/// </summary>
internal sealed class ReferenceLookup(IntegrationDbContext db)
{
    /// <summary>
    /// The external id, or <c>null</c> when this connection has no reference for that entity.
    ///
    /// <para>
    /// The tenant is a parameter and the query filter is bypassed for it, because the callers
    /// include a Hangfire dispatcher and another module's consumer, neither of which has an
    /// ambient tenant. A reference of another tenant is therefore invisible rather than
    /// forbidden — the 404-never-403 rule, applied at the source.
    /// </para>
    /// </summary>
    internal async Task<string?> GetExternalIdAsync(
        Guid tenantId, Guid connectionId, string entityType, Guid crmId, CancellationToken ct)
    {
        // Guarded rather than queried: the unique index needs all four, and asking the database
        // for a reference keyed on Guid.Empty is a scan whose answer is already known.
        if (tenantId == Guid.Empty || connectionId == Guid.Empty
            || crmId == Guid.Empty || string.IsNullOrWhiteSpace(entityType))
            return null;

        var trimmed = entityType.Trim();

        return await db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                        && r.ConnectionId == connectionId
                        && r.EntityType == trimmed
                        && r.CrmId == crmId)
            .Select(r => r.ExternalId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The reverse direction, which the inbound synchronisation and the reconciliation need: the
    /// CRM entity an external identifier belongs to, or <c>null</c>.
    ///
    /// <para>
    /// It exists here and not in the caller because the uniqueness guarantee is two-way per
    /// connection (INT-07, criterion 2), and a reverse lookup written ad hoc by each caller is
    /// how one of them ends up ignoring the connection and matching another insurer's number.
    /// </para>
    /// </summary>
    internal async Task<Guid?> GetCrmIdAsync(
        Guid tenantId, Guid connectionId, string entityType, string externalId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || connectionId == Guid.Empty
            || string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(externalId))
            return null;

        var trimmedType = entityType.Trim();
        var trimmedId = externalId.Trim();

        return await db.References
            .IgnoreQueryFilters()
            .Where(r => r.TenantId == tenantId
                        && r.ConnectionId == connectionId
                        && r.EntityType == trimmedType
                        && r.ExternalId == trimmedId)
            .Select(r => (Guid?)r.CrmId)
            .FirstOrDefaultAsync(ct);
    }
}
