namespace Sankore.Modules.Customers.Infrastructure;

using Sankore.Modules.Customer360.PublicApi;
using Sankore.Modules.Customers.PublicApi;

/// <summary>
/// Serves the older <see cref="ICustomerModule"/> contract from the real M01 module,
/// replacing <c>Sankore.Api.Stubs.StubCustomerModule</c>.
///
/// It exists so modules already written against the Customer360 contract (Leads'
/// convert-to-existing-customer check, for instance) keep compiling and start hitting
/// real data, without M01 having to adopt that shape as its own contract. New callers
/// should take <see cref="ICustomersModule"/> directly.
/// </summary>
internal sealed class LegacyCustomerModuleAdapter(ICustomersModule inner) : ICustomerModule
{
    /// <summary>
    /// The legacy contract says "exists"; the honest mapping is "exists and is usable".
    /// Its only caller gates conversion on it, and attaching business to a suspended,
    /// archived or merged record would be wrong — so an inactive client answers false.
    /// </summary>
    public Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct)
        => inner.ExistsAndActiveAsync(tenantId, customerId, ct);

    /// <summary>
    /// Email and PhoneNumber are deliberately left null: both are encrypted at rest in
    /// M01 and only reachable through the audited reveal endpoint. No sensitive data
    /// crosses this boundary — a legacy caller that needs a contact detail has to ask
    /// for it explicitly, under its own user's identity, and be logged doing so.
    /// </summary>
    public async Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct)
    {
        var summary = await inner.GetClientSummaryAsync(tenantId, customerId, ct);

        return summary is null
            ? null
            : new CustomerSummary(
                Id: summary.Id,
                FullName: summary.DisplayName,
                Email: null,
                PhoneNumber: null,
                AgencyId: summary.AgencyId);
    }
}
