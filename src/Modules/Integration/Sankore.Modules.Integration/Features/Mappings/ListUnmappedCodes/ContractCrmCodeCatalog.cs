namespace Sankore.Modules.Integration.Features.Mappings.ListUnmappedCodes;

using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Integration.Domain;

/// <summary>
/// What the CRM's own code lists are REACHABLE as, through module contracts only.
///
/// <para>
/// This module may reference a <c>*.PublicApi</c> assembly and nothing else, and today two of the
/// eight domains have a path to an enumeration:
/// </para>
///
/// <list type="bullet">
/// <item><b>Agency</b> — partial. <c>IAdministrationModule</c> has no "list agencies": it exposes
/// <c>GetAgencyAsync(tenantId, agencyId)</c>, a lookup by id. The only way to obtain ids without
/// touching the <c>administration</c> schema is <c>GetAvailableAgentsAsync</c>, which yields the
/// agencies of the agents currently AVAILABLE for lead dispatching — so an agency with no
/// available commercial agent is invisible here. Reported as
/// <see cref="CrmCodeListAvailability.Partial"/> for that reason, never as a complete list.</item>
///
/// <item><b>Product</b> — complete. <c>IAdministrationModule.ListProductsAsync</c> enumerates the
/// tenant's catalogue, which is what this domain wants: the product codes an administrator
/// maintains in M12. It was reported <see cref="CrmCodeListAvailability.Unavailable"/> for as long
/// as the contract offered only <c>GetProductCategoryAsync(tenantId, productCode)</c> — a lookup
/// that takes the code it would have to return — and the tempting substitute, the
/// <c>ProductCategory</c> enum, is a closed list of CATEGORIES: it would have answered "Loan,
/// Savings, Tontine…" where the codes were wanted, and a plausible-looking wrong list is worse
/// than an empty one. Retired products are INCLUDED: a mapping to the CBS outlives the day the
/// institution stops selling the product, and a code vanishing from this list would read as
/// "mapped" rather than as "no longer offered".</item>
///
/// <item><b>IdDocType, Gender, MaritalStatus, Country, Profession, Sector</b> — none. These are
/// M01's enums and M01's per-tenant reference data, living in its MAIN assembly; no contract
/// projects them, and referencing that assembly is forbidden.</item>
/// </list>
///
/// <para>
/// Each unavailable domain therefore answers an explicit empty list carrying the sentence above,
/// so the screen can say "cannot be checked" instead of "nothing missing". Adding a domain is an
/// edit of this one file, once a contract exposes the list.
/// </para>
/// </summary>
internal sealed class ContractCrmCodeCatalog(IAdministrationModule administration) : ICrmCodeCatalog
{
    public async Task<CrmCodeList> GetAsync(Guid tenantId, MappingDomain domain, CancellationToken ct)
        => domain switch
        {
            MappingDomain.Agency => await AgenciesAsync(tenantId, ct),

            MappingDomain.Product => await ProductsAsync(tenantId, ct),

            MappingDomain.IdDocType or MappingDomain.Gender or MappingDomain.MaritalStatus
                or MappingDomain.Country or MappingDomain.Profession or MappingDomain.Sector =>
                CrmCodeList.Unavailable(
                    $"Not available through a module contract: the CRM's {domain} code list belongs "
                    + "to the Customers module (M01) and is not projected by ICustomersModule. "
                    + "Mappings for this domain can be created, imported and exported; only the "
                    + "'what is still unmapped' check cannot be computed."),

            _ => CrmCodeList.Unavailable($"Unknown mapping domain '{domain}'.")
        };

    private async Task<CrmCodeList> ProductsAsync(Guid tenantId, CancellationToken ct)
    {
        const string source =
            "COMPLETE — IAdministrationModule.ListProductsAsync. Every product of the tenant's "
            + "catalogue, retired ones included: a code mapped to the CBS stays meaningful after "
            + "the institution stops selling the product.";

        // category null: the whole catalogue. A mapping domain is not category-specific — a CBS
        // translates loans, savings and insurance codes through the same table.
        var products = await administration.ListProductsAsync(tenantId, null, ct);

        return new CrmCodeList(
            CrmCodeListAvailability.Complete,
            source,
            products
                .Select(p => new CrmCode(p.Code, p.Name))
                .OrderBy(c => c.Code, StringComparer.Ordinal)
                .ToList());
    }

    private async Task<CrmCodeList> AgenciesAsync(Guid tenantId, CancellationToken ct)
    {
        const string source =
            "PARTIAL — derived from IAdministrationModule.GetAvailableAgentsAsync + GetAgencyAsync. "
            + "The contract has no agency enumeration, so only agencies that currently have an "
            + "available commercial agent appear. An empty result does NOT prove every agency is "
            + "mapped.";

        // agencyId null: every available agent of the tenant, whatever their agency.
        var agents = await administration.GetAvailableAgentsAsync(tenantId, null, ct);

        var agencyIds = agents.Select(a => a.AgencyId).Distinct().ToList();

        var codes = new Dictionary<string, CrmCode>(StringComparer.Ordinal);

        // One lookup per distinct agency, bounded by the number of agencies a tenant has — tens,
        // not thousands. A batch projection would be the right fix, and it belongs in the
        // Administration contract rather than here.
        foreach (var agencyId in agencyIds)
        {
            var agency = await administration.GetAgencyAsync(tenantId, agencyId, ct);

            // Null means soft-deleted or absent in that tenant: a dangling reference degrades to
            // "skip", never to an assumption that it resolves.
            if (agency is null || !agency.IsActive) continue;

            codes[agency.Code] = new CrmCode(agency.Code, agency.Name);
        }

        return new CrmCodeList(
            CrmCodeListAvailability.Partial,
            source,
            codes.Values.OrderBy(c => c.Code, StringComparer.Ordinal).ToList());
    }
}
