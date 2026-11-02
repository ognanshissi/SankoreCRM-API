namespace Sankore.Modules.Administration.Infrastructure;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Reads the company profile's currency. Scoped, and remembered for the request: a lead import or
/// a form with several amounts asks once per tenant, not once per amount.
/// </summary>
/// <remarks>
/// Every caller is ANOTHER module's command, inside its ambient <see cref="TransactionScope"/>. The
/// read is suppressed out of it, as every other read of this module's facade is by its callers:
/// this context is the request's single <see cref="AdministrationDbContext"/>, and once its
/// connection has enlisted in the ambient transaction EF refuses to use it in a suppressed scope
/// (« This connection was used with an ambient transaction… »). Creating a credit project with an
/// amount read the currency first, then resolved its product line suppressed — a 500 (2026-11-02).
/// </remarks>
internal sealed class InstitutionCurrency(AdministrationDbContext db) : IInstitutionCurrency
{
    private readonly Dictionary<Guid, string> _known = new();

    public async Task<string> GetAsync(Guid tenantId, CancellationToken ct)
    {
        if (_known.TryGetValue(tenantId, out var known)) return known;

        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);

        // IgnoreQueryFilters + explicit tenant: the caller may be a job outside the ambient tenant.
        var currency = await db.CompanyInfos
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.Currency)
            .FirstOrDefaultAsync(ct);
        suppressed.Complete();

        return _known[tenantId] = string.IsNullOrWhiteSpace(currency) ? InstitutionCurrencies.Default : currency;
    }
}
