namespace Sankore.Api.Stubs;

using Sankore.Modules.Customer360.PublicApi;

/// <summary>
/// Temporary stub — returns false/null for all queries until the
/// Customer 360 module (M01) is scaffolded and registered.
/// </summary>
internal sealed class StubCustomerModule : ICustomerModule
{
    public Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct)
        => Task.FromResult(true); // Permissive: allow conversion even without a real Customer module

    public Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct)
        => Task.FromResult<CustomerSummary?>(null);
}
