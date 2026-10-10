using Sankore.Shared.Kernel.ValueObject;

namespace Sankore.Modules.Administration.PublicApi;

using Sankore.Shared.Kernel;

/// <summary>
/// The ONLY entry point other modules (e.g. Leads) are allowed to use to
/// read data owned by the Users module. No other module may reference
/// Sankore.Modules.Administration directly or its DbContext — only this interface
/// and the DTOs below, both living in this small, dependency-free assembly.
/// </summary>
public interface IAdministrationModule
{
    /// <summary>
    /// Returns commercial agents currently available for lead dispatching,
    /// optionally filtered to a specific agency. Used by
    /// Leads.Features.DispatchLead.DispatchLeadHandler.
    /// </summary>
    Task<IReadOnlyList<AgentSummary>> GetAvailableAgentsAsync(
        Guid tenantId,
        Guid? agencyId,
        CancellationToken ct);

    Task<AgentSummary?> GetAgentAsync(Guid agentId, CancellationToken ct);

    /// <summary>
    /// Non-sensitive projection of one agency, or <c>null</c> when it does not exist
    /// (or has been soft-deleted) in that tenant.
    ///
    /// Added for module M01 (Customers), which stamps the agency CODE onto every
    /// client record so a client number can be minted per agency and per year
    /// (<c>{AgencyCode}-{YYYY}-{Seq:6}</c>) without M01 ever reading the
    /// <c>administration</c> schema. <c>AgentSummary</c> only carries an agency id,
    /// hence this second, agency-shaped projection.
    ///
    /// <paramref name="tenantId"/> is explicit because callers may run outside an HTTP
    /// request (Hangfire jobs, MassTransit consumers) where the ambient tenant context
    /// is not the tenant being operated on.
    /// </summary>
    Task<AgencySummary?> GetAgencyAsync(Guid tenantId, Guid agencyId, CancellationToken ct);

    /// <summary>
    /// Returns the IDs of agents managed by the given supervisor (same agency or sub-agencies).
    /// Used for task visibility scoping (US-M13-090).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetTeamAgentIdsAsync(Guid tenantId, Guid supervisorId, CancellationToken ct);

    /// <summary>
    /// Returns the product category (Loan, Savings, Tontine) for a given product code.
    /// Used by Leads module for qualification template resolution.
    /// Returns null if the product code is not found.
    /// </summary>
    Task<string?> GetProductCategoryAsync(Guid tenantId, string productCode, CancellationToken ct);

    /// <summary>
    /// One catalogue entry by its code, or <c>null</c> when this tenant has no such product.
    ///
    /// <para>
    /// The second, product-shaped projection next to <see cref="GetProductCategoryAsync"/>, for
    /// the reason <see cref="GetAgencyAsync"/> exists next to <see cref="GetAgentAsync"/>: a
    /// consumer that has to decide whether a product may still be SOLD needs its activity flag,
    /// and a category string cannot carry one. M-Integration's insurance catalogue is the first
    /// caller — a withdrawn catalogue entry must stop new subscriptions at the counter — and
    /// reading the flag through a second call to the category lookup is not possible.
    /// </para>
    ///
    /// <para>
    /// The code is matched the way M12 stores it: upper-cased. A caller passes whatever it holds.
    /// </para>
    /// </summary>
    Task<ProductSummary?> GetProductAsync(Guid tenantId, string productCode, CancellationToken ct);

    /// <summary>
    /// Every product of the tenant, newest-sorted by code, optionally narrowed to one category.
    ///
    /// <para>
    /// The ENUMERATION the contract was missing. Two callers need it and neither can be served by
    /// a lookup that takes the code it would have to return: M-Integration's "which CRM codes are
    /// still unmapped" screen (INT-04), which until now reported the <c>Product</c> domain as
    /// unavailable, and any screen offering an administrator the catalogue entries an insurance
    /// product may be attached to.
    /// </para>
    ///
    /// <para>
    /// <paramref name="category"/> is a <see cref="ProductCategory"/> name, compared
    /// case-insensitively; an unrecognised value returns an EMPTY list rather than every product,
    /// because a filter silently ignored is how a caller asking for insurance products gets loans.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<ProductSummary>> ListProductsAsync(
        Guid tenantId, string? category, CancellationToken ct);

    /// <summary>
    /// Returns the email provider configuration for a tenant so that the
    /// Notifications module can resolve the correct provider at send time.
    /// Returns null when no custom config exists (use platform default).
    /// </summary>
    Task<TenantNotificationConfigDto?> GetNotificationConfigAsync(
        Guid tenantId, CancellationToken ct);

    /// <summary>
    /// Counts one email against the tenant's monthly allowance, or refuses it. Check and
    /// increment happen together so two concurrent senders cannot both slip past the limit.
    /// A tenant with no configured limit is always granted — and still counted, so usage stays
    /// visible before anyone sets one.
    /// </summary>
    Task<EmailQuotaDecision> TryConsumeEmailQuotaAsync(Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Read-only projection of tenant email provider settings exposed to
/// the Notifications module. Credentials are never included — only the
/// vault reference path.
/// </summary>
/// <summary>
/// A tenant's effective email configuration — everything EXCEPT the credential itself, which
/// the consumer fetches from the vault with <see cref="NotificationSecrets.CredentialKey"/>.
/// Keeping the secret out of this DTO is what lets M08 cache the rest in Redis.
/// </summary>
public sealed record TenantNotificationConfigDto(
    string ProviderType,
    bool UseDefaultPlatformProvider,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    int? MonthlyQuotaLimit,
    /// <summary>Whether a credential was stored for this provider. Never the credential.</summary>
    bool HasCredential,
    // ── SMTP relay settings, meaningful when ProviderType is "Smtp" ─────────
    string? SmtpHost,
    int? SmtpPort,
    string? SmtpUsername,
    bool SmtpUseSsl,
    bool SmtpUseStartTls);

/// <summary>
/// Read-only projection of an agency, safe to hand to other modules: identifier,
/// business code, display name, place in the hierarchy and activity flag — nothing
/// that would let a consumer bypass the Administration module's own rules.
/// </summary>
public sealed record AgencySummary(
    Guid Id,
    string Code,
    string Name,
    Guid? ParentAgencyId,
    bool IsActive);

/// <summary>
/// Read-only projection of an agent, safe to hand to other modules.
/// Deliberately NOT the same type as the Users module's internal User
/// entity — this decouples Leads from any future change to that entity.
/// </summary>
public sealed record AgentSummary(
    Guid Id,
    string FullName,
    Guid AgencyId,
    IReadOnlyList<string> SpokenLanguages,
    IReadOnlyList<string> Specialties,
    GeoPoint? CurrentLocation,
    int ActiveLeadsCount,
    int HotLeadsCount,
    double ConversionRate30d,
    bool IsAvailable);

/// <param name="Limit">Null when the tenant has no monthly limit.</param>
public sealed record EmailQuotaDecision(bool Granted, int? Limit, int UsedThisMonth);

/// <summary>
/// Read-only projection of one product of the tenant's catalogue, safe to hand to other modules.
///
/// <para>
/// Four fields and deliberately not the whole entity: <c>ParametersJson</c> is a category-dependent
/// free-form payload whose schema the owning module validates, and the CBS link
/// (<c>BusinessProductId</c>) belongs to M12's own integration screen. A consumer needs the
/// identity (<paramref name="Code"/>), something to show (<paramref name="Name"/>), the axis it
/// reasons on (<paramref name="Category"/>) and whether the institution still sells it
/// (<paramref name="IsActive"/>).
/// </para>
/// </summary>
/// <param name="Code">Upper-cased, unique per tenant. The value other modules store.</param>
/// <param name="Category">A <see cref="ProductCategory"/> name.</param>
/// <param name="IsActive">
/// <c>false</c> once the product has been retired from the catalogue. A retirement never deletes
/// the row, so a code other modules hold keeps resolving — it resolves to an inactive product.
/// </param>
public sealed record ProductSummary(
    string Code,
    string Name,
    string Category,
    bool IsActive);
