namespace Sankore.Modules.Administration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the company profile's currency. Scoped, and remembered for the request: a lead import or
/// a form with several amounts asks once per tenant, not once per amount.
/// </summary>
internal sealed class InstitutionCurrency(AdministrationDbContext db) : IInstitutionCurrency
{
    private readonly Dictionary<Guid, string> _known = new();

    public async Task<string> GetAsync(Guid tenantId, CancellationToken ct)
    {
        if (_known.TryGetValue(tenantId, out var known)) return known;

        // IgnoreQueryFilters + explicit tenant: the caller may be a job outside the ambient tenant.
        var currency = await db.CompanyInfos
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.Currency)
            .FirstOrDefaultAsync(ct);

        return _known[tenantId] = string.IsNullOrWhiteSpace(currency) ? InstitutionCurrencies.Default : currency;
    }
}
