namespace Sankore.Modules.Customer360.PublicApi;

/// <summary>
/// Public contract exposed by the Customer 360 module.
/// Other modules reference only this interface — never the main assembly.
/// </summary>
public interface ICustomerModule
{
    /// <summary>
    /// Checks whether a customer with the given ID exists for the tenant.
    /// Used by Leads module for convert-to-existing-customer validation (US-M13-171).
    /// </summary>
    Task<bool> ExistsAsync(Guid tenantId, Guid customerId, CancellationToken ct);

    /// <summary>
    /// Returns a lightweight summary of a customer, or null if not found.
    /// </summary>
    Task<CustomerSummary?> GetCustomerAsync(Guid tenantId, Guid customerId, CancellationToken ct);
}

public sealed record CustomerSummary(
    Guid Id,
    string FullName,
    string? Email,
    string? PhoneNumber,
    Guid? AgencyId);
