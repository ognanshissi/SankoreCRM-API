using Microsoft.Extensions.Logging;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// Stable EventId constants for security-relevant log entries produced by
/// tenant resolution. Use structured log queries on these IDs for BCEAO audit.
/// </summary>
internal static class SecurityEvents
{
    /// <summary>Caller sent a raw x-tenant-id header — trivially falsifiable, rejected.</summary>
    public static readonly EventId RawTenantIdHeader = new(4001, "RawTenantIdHeader");

    /// <summary>JWT tenant claim and Host-resolved tenant differ — possible token replay.</summary>
    public static readonly EventId CrossTenantTokenReplay = new(4002, "CrossTenantTokenReplay");

    /// <summary>Tenant resolved from FQDN is inactive or unrecognised.</summary>
    public static readonly EventId InactiveTenant = new(4003, "InactiveTenant");
}
