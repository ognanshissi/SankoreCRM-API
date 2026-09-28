namespace Sankore.Modules.Customers.Features.Lifecycle;

using Sankore.Modules.Customers.Domain;

/// <summary>
/// Uniform payload returned by every lifecycle mutation of this zone.
/// The caller cannot always predict the resulting state (reactivating lands on
/// <c>Active</c> or back on <c>PendingKyc</c> depending on the KYC status; a
/// transfer may drop the advisor), so the new state is echoed instead of a bare
/// 204 — and <c>Version</c> lets the UI keep its optimistic-concurrency token
/// fresh without a second round trip.
///
/// Enum values travel as their NAME, like everywhere else in the module, so the
/// front-end never depends on ordinals. No sensitive field appears here.
/// </summary>
public sealed record ClientLifecycleStateDto(
    Guid ClientId,
    string Status,
    string KycStatus,
    string RiskLevel,
    Guid AgencyId,
    string AgencyCode,
    Guid? AdvisorUserId,
    DateTimeOffset? ArchivedAt,
    uint Version);

internal static class ClientLifecycleStateMapper
{
    internal static ClientLifecycleStateDto ToLifecycleState(this Client client) => new(
        ClientId: client.Id,
        Status: client.Status.ToString(),
        KycStatus: client.KycStatus.ToString(),
        RiskLevel: client.RiskLevel.ToString(),
        AgencyId: client.AgencyId,
        AgencyCode: client.AgencyCode,
        AdvisorUserId: client.AdvisorUserId,
        ArchivedAt: client.ArchivedAt,
        Version: client.Version);
}

/// <summary>Identity used by this zone's transitions when no human is behind them.</summary>
internal static class LifecycleActors
{
    /// <summary>
    /// Actor id recorded for every transition triggered by an incoming KYC event.
    /// <see cref="Guid.Empty"/> is the repo-wide SYSTEM account (the same value as
    /// <c>BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM")</c>), so the
    /// audit trail and the status-history line both attribute the change to SYSTEM
    /// rather than to whichever user happened to trigger the upstream call.
    /// </summary>
    internal static readonly Guid System = Guid.Empty;
}
