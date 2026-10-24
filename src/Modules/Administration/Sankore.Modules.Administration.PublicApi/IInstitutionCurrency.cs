namespace Sankore.Modules.Administration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The currency of a tenant's amounts, set by its administrator on the company profile. Every
/// module that writes an amount typed by a user resolves its currency through
/// <see cref="InstitutionCurrencyExtensions.ResolveAsync"/>.
/// </summary>
public interface IInstitutionCurrency
{
    /// <summary>
    /// The tenant's currency; <see cref="InstitutionCurrencies.Default"/> when its profile does not
    /// exist yet. The tenant is explicit: jobs, consumers and webhooks run outside the ambient one.
    /// </summary>
    Task<string> GetAsync(Guid tenantId, CancellationToken ct);
}

public static class InstitutionCurrencyExtensions
{
    /// <summary>
    /// <see cref="InstitutionCurrencies.Resolve"/> against the tenant's currency. A null service —
    /// a handler built by hand in a unit test — reads as <see cref="InstitutionCurrencies.Default"/>.
    /// </summary>
    public static async Task<Result<string>> ResolveAsync(
        this IInstitutionCurrency? currencies, Guid tenantId, string? requested, string? stored, CancellationToken ct)
    {
        var institution = currencies is null ? InstitutionCurrencies.Default : await currencies.GetAsync(tenantId, ct);
        return InstitutionCurrencies.Resolve(requested, institution, stored);
    }

    public static async Task<string> GetOrDefaultAsync(this IInstitutionCurrency? currencies, Guid tenantId, CancellationToken ct)
    {
        var institution = currencies is null ? null : await currencies.GetAsync(tenantId, ct);
        return string.IsNullOrWhiteSpace(institution) ? InstitutionCurrencies.Default : institution;
    }
}
