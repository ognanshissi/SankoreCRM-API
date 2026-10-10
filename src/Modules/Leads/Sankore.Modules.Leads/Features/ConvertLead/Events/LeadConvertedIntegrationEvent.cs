namespace Sankore.Modules.Leads.Features.ConvertLead.Events;

using Sankore.Shared.Kernel;

/// <summary>
/// Published via the outbox when a lead is successfully converted.
/// Consumed by Analytics/Notifications for conversion tracking — the client itself is created
/// synchronously through <c>ICustomersModule.CreateFromLeadAsync</c>, not from this event.
/// </summary>
/// <param name="ConversionType">
/// Which of BNF-B-12's two paths ran — see <see cref="LeadConversionType"/>. Carried because a
/// consumer cannot derive it: both paths end with a lead pointing at a client, and only the
/// handler knows whether a client was created or an existing one was reused.
/// </param>
/// <param name="ProductLineId">
/// The resolved M12 product line (BNF-B-24), or null when nobody resolved it. What a back-office
/// push needs in order to know which system receives the subscription and on which line.
/// </param>
public sealed record LeadConvertedIntegrationEvent(
    Guid LeadId,
    Guid CustomerId,
    Guid TenantId,
    string FullName,
    string PhoneNumber,
    string? Email,
    DateTimeOffset ConvertedAt,
    string ConversionType = LeadConversionType.NewCustomer,
    Guid? ProductLineId = null) : IntegrationEventBase;

/// <summary>
/// The two conversion paths of BNF-B-12/B-24.
///
/// <para>
/// Strings and not an enum, for the same reason every other cross-module concept on these contracts
/// is a string: a consumer compares by name and must not have to reference this assembly.
/// </para>
/// </summary>
public static class LeadConversionType
{
    /// <summary>A new prospect: M01 created the client record as part of the conversion.</summary>
    public const string NewCustomer = "NewCustomer";

    /// <summary>
    /// A known customer asking for another product (BNF-B-22/B-24): no client was created, and
    /// nothing of their identity was re-entered.
    /// </summary>
    public const string ExistingCustomerNewProduct = "ExistingCustomerNewProduct";
}
