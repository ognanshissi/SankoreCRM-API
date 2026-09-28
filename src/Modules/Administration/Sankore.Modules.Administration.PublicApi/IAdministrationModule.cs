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
    /// Returns the email provider configuration for a tenant so that the
    /// Notifications module can resolve the correct provider at send time.
    /// Returns null when no custom config exists (use platform default).
    /// </summary>
    Task<TenantNotificationConfigDto?> GetNotificationConfigAsync(
        Guid tenantId, CancellationToken ct);
}

/// <summary>
/// Read-only projection of tenant email provider settings exposed to
/// the Notifications module. Credentials are never included — only the
/// vault reference path.
/// </summary>
public sealed record TenantNotificationConfigDto(
    string ProviderType,
    bool UseDefaultPlatformProvider,
    string? FromEmail,
    string? FromName,
    string? ReplyToEmail,
    string? SendingDomain,
    string? CredentialVaultPath,
    int? MonthlyQuotaLimit);

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
