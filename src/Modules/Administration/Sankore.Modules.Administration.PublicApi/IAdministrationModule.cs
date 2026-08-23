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
