namespace Sankore.Modules.Customer360.PublicApi;

/// <summary>
/// Public contract exposed by the Customer 360 module.
/// Other modules reference only this interface — never the main assembly.
/// </summary>
public interface ICustomerModule
{

}

public sealed record CustomerSummary(
    Guid Id,
    string FullName,
    string? Email,
    string? PhoneNumber,
    Guid? AgencyId);
