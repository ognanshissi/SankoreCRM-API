namespace Sankore.Modules.Integration.Features.Mappings;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// The one place a CRM code becomes an external code, and back (INT-04, criterion 4).
///
/// <para>
/// Every adapter and the dispatcher go through this rather than reading
/// <c>db.Mappings</c> themselves, because the interesting part is the FAILURE: a missing row is a
/// <see cref="ErrorFamily.Technical"/> result naming the domain and the code, never a
/// pass-through of the CRM code. Sending an unmapped code is how a customer is created at the CBS
/// with a profession nobody can read, and the row looks successful.
/// </para>
///
/// <para>
/// <see cref="Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}"/>
/// paired with an explicit tenant predicate, in both directions: the dispatcher calls this from a
/// Hangfire job and a MassTransit consumer, where <c>ITenantContext.CurrentTenantId</c> is not the
/// tenant being worked on. The predicate is not optional — dropping it is a cross-tenant read.
/// </para>
/// </summary>
internal sealed class MappingResolver(IntegrationDbContext db)
{
    /// <summary>
    /// The external code for a CRM code, or a <c>Technical</c> failure
    /// (<see cref="IntegrationErrors.MappingMissing"/>) whose detail names the domain and the
    /// code — criterion 4 of INT-04.
    /// </summary>
    public async Task<IntegrationResult<string>> ResolveAsync(
        Guid tenantId,
        Guid connectionId,
        MappingDomain domain,
        string crmCode,
        CancellationToken ct)
    {
        // Trimmed, never case-folded: an external system's codes are its own and the unique
        // index is on the raw value, so folding here would match a row the database does not.
        var code = crmCode?.Trim() ?? string.Empty;

        var external = await db.Mappings
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId
                        && m.ConnectionId == connectionId
                        && m.Domain == domain
                        && m.CrmCode == code)
            .Select(m => m.ExternalCode)
            .FirstOrDefaultAsync(ct);

        return external is null
            ? IntegrationResult.Technical<string>(IntegrationErrors.MappingMissing, MissingDetail(domain, code))
            : IntegrationResult.Ok(external);
    }

    /// <summary>
    /// The CRM code for an external code, or <c>null</c>.
    ///
    /// <para>
    /// Null rather than a failure, because this serves the CBS→CRM translation of the customer
    /// snapshot (INT-21): a synchronisation that meets one unknown external code must store the
    /// row with that field untranslated, not abandon the whole stream. The caller decides what an
    /// unknown code means to it.
    /// </para>
    ///
    /// <para>
    /// The reverse index is deliberately not unique — two CRM codes may fold onto one external
    /// code — so this returns the FIRST match, ordered by the CRM code, to be deterministic
    /// rather than dependent on physical row order.
    /// </para>
    /// </summary>
    public async Task<string?> ReverseAsync(
        Guid tenantId,
        Guid connectionId,
        MappingDomain domain,
        string externalCode,
        CancellationToken ct)
    {
        var code = externalCode?.Trim() ?? string.Empty;

        return await db.Mappings
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId
                        && m.ConnectionId == connectionId
                        && m.Domain == domain
                        && m.ExternalCode == code)
            .OrderBy(m => m.CrmCode)
            .Select(m => m.CrmCode)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Criterion 4, verbatim: the detail names the domain and the code. Kept in one method so
    /// every caller's message reads the same, and so a test can assert the shape once.
    /// </summary>
    internal static string MissingDetail(MappingDomain domain, string crmCode)
        => $"No mapping for domain '{domain}' and CRM code '{crmCode}'.";
}
