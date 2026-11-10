namespace Sankore.Modules.Administration.Infrastructure;

using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// <see cref="IQuoteIssuer"/>. Both methods run outside the caller's ambient transaction, for the
/// reason <see cref="InstitutionCurrency"/> gives; the number is moreover committed on its own on
/// purpose, so two quotes generated at the same instant never share one.
/// </summary>
internal sealed class QuoteIssuerReader(AdministrationDbContext db, ITenantStore? tenants = null) : IQuoteIssuer
{
    public async Task<QuoteIssuer> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var team = await TeamAsync(tenantId, ct);
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var info = await db.CompanyInfos.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == team, ct);
        suppressed.Complete();
        return ToIssuer(info);
    }

    public async Task<string> NextNumberAsync(Guid tenantId, DateOnly issuedOn, CancellationToken ct)
    {
        var issuer = await GetAsync(tenantId, ct);
        var rank = await TakeAsync(tenantId, issuedOn.Year, ct);
        return CompanyInfo.FormatQuoteNumber(issuer.QuotePrefix, issuedOn.Year, rank);
    }

    /// <summary>
    /// One statement on PostgreSQL — an upsert that returns the new value — so two callers cannot
    /// read the same last number. The InMemory provider (tests) has no SQL and goes through the
    /// entity instead.
    /// </summary>
    private async Task<long> TakeAsync(Guid tenantId, int year, CancellationToken ct)
    {
        using var own = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        long rank;
        if (db.Database.IsRelational())
        {
            var values = await db.Database.SqlQuery<long>($"""
                INSERT INTO administration.quote_number_counters (tenant_id, year, last_number)
                VALUES ({tenantId}, {year}, 1)
                ON CONFLICT (tenant_id, year)
                DO UPDATE SET last_number = administration.quote_number_counters.last_number + 1
                RETURNING last_number AS "Value"
                """).ToListAsync(ct);
            rank = values[0];
        }
        else
        {
            var counter = await db.QuoteNumberCounters.IgnoreQueryFilters().AsTracking()
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Year == year, ct);
            if (counter is null) db.QuoteNumberCounters.Add(counter = QuoteNumberCounter.Start(tenantId, year));
            rank = counter.Next();
            await db.SaveChangesAsync(ct);
        }
        own.Complete();
        return rank;
    }

    public async Task<IReadOnlyList<QuotableProduct>> GetProductsAsync(
        Guid tenantId, IReadOnlyCollection<Guid> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return [];
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        // The DATA tenant: products are per mode, unlike the institution's identity.
        var products = await db.ProductSpecialities.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.TenantId == tenantId && productIds.Contains(p.Id))
            .Select(p => new QuotableProduct(p.Id, p.Code, p.Name, p.Category.ToString(), p.IsActive,
                p.UnitPrice, p.PriceUnit, p.VatCategory))
            .ToListAsync(ct);
        suppressed.Complete();
        return products;
    }

    public async Task<QuoteLogo?> GetLogoAsync(Guid tenantId, CancellationToken ct)
    {
        var team = await TeamAsync(tenantId, ct);
        using var suppressed = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        var logo = await db.CompanyLogos.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.TenantId == team)
            .Select(l => new QuoteLogo(l.Content, l.ContentType))
            .FirstOrDefaultAsync(ct);
        suppressed.Complete();
        return logo;
    }

    /// <summary>The number the next quote of <paramref name="year"/> would take, without taking it.</summary>
    internal static async Task<string> PeekAsync(AdministrationDbContext db, Guid tenantId, string prefix, int year, CancellationToken ct)
    {
        var last = await db.QuoteNumberCounters.IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Year == year)
            .Select(c => (long?)c.LastNumber)
            .FirstOrDefaultAsync(ct) ?? 0;
        return CompanyInfo.FormatQuoteNumber(prefix, year, last + 1);
    }

    internal static QuoteIssuer ToIssuer(CompanyInfo? c) => c is null
        ? new QuoteIssuer(
            string.Empty, null, null, null, null, null, null, null, null, null, null,
            InstitutionCurrencies.Default, CompanyInfo.DefaultVatStandardRate, CompanyInfo.DefaultVatReducedRate, null,
            CompanyInfo.DefaultQuotePrefix, CompanyInfo.DefaultQuoteValidityDays, null)
        : new QuoteIssuer(
            c.Name, c.LegalName, c.LegalForm, c.HeadOfficeAddress, c.LegalPhone, c.LegalEmail, c.Website,
            c.Rccm, c.TaxpayerNumber, c.TaxRegime, c.QuoteFooter,
            string.IsNullOrWhiteSpace(c.Currency) ? InstitutionCurrencies.Default : c.Currency,
            c.VatStandardRate, c.VatReducedRate, c.MaxDiscountPercent,
            c.QuotePrefix, c.QuoteValidityDays, c.QuotePaymentTerms);

    private async Task<Guid> TeamAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenants is null || tenantId == Guid.Empty) return TenantTwins.TeamOf(tenantId);
        return (await tenants.GetAsync(tenantId, ct))?.TeamTenantId ?? TenantTwins.TeamOf(tenantId);
    }
}
