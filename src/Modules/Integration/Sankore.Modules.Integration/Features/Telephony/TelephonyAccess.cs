namespace Sankore.Modules.Integration.Features.Telephony;

using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;

/// <summary>
/// "Is telephony on for this tenant, and what can it do" — asked once, here, by every slice of
/// the area (TEL-B-01).
///
/// <para>
/// <b>The active connection IS the switch, and there is deliberately no second one.</b> TEL-B-01
/// asks for a <c>TELEPHONY_ENABLED</c> tenant setting; this module answers it with the state it
/// already holds. M12 exposes no generic settings store — each module owns its own, and the one
/// typed table it does own, <c>compliance_settings</c>, is read by three modules for regulatory
/// values and is not where a telephony feature flag belongs. A ninth store whose value could
/// disagree with "a connection is active" would give an administrator two places to look and one
/// state nobody can explain: enabled with nothing to dial, or disabled with a provider delivering
/// webhooks. Deactivating the connection is the one-click switch, it is audited, and it already
/// stops the webhook URL working.
/// </para>
///
/// <para>
/// Every refusal here is <see cref="TelephonyErrors.Disabled"/> and never a 404: an administrator
/// must read a setting to change rather than a bug to report.
/// </para>
/// </summary>
internal sealed class TelephonyAccess(IntegrationAdapterResolver resolver)
{
    /// <summary>
    /// The tenant's telephony connection and its adapter, or <see cref="TelephonyErrors.Disabled"/>.
    ///
    /// <para>
    /// Takes the tenant explicitly rather than reading <c>ITenantContext</c>: the daily link check
    /// and the catch-up sweep are Hangfire jobs, where the tenant is an argument and the ambient
    /// context is SYSTEM.
    /// </para>
    /// </summary>
    internal async Task<IntegrationResult<ResolvedAdapter>> ResolveAsync(
        Guid tenantId, CancellationToken ct)
    {
        var resolved = await resolver.ResolveAsync(tenantId, IntegrationFamily.Telephony, ct);

        // The resolver answers NoActiveConnection or AdapterNotRegistered, and from the outside
        // those are the same fact: nothing can be dialled. Translated here so the HTTP layer maps
        // one code, and logged by the caller with the resolver's own detail intact.
        return resolved.IsFailure
            ? IntegrationResult.Functional<ResolvedAdapter>(TelephonyErrors.Disabled, resolved.Detail)
            : resolved;
    }

    /// <summary>
    /// The port for one capability, with the two refusals kept apart.
    ///
    /// <para>
    /// <see cref="TelephonyErrors.Disabled"/> means the tenant has no telephony at all;
    /// <c>INTEGRATION_CAPABILITY_NOT_SUPPORTED</c> means it has a provider that does not do this
    /// one thing — a provider with no recordings, or none that lists its users. Collapsing them
    /// would send an administrator to the connection screen for a limitation of their provider.
    /// </para>
    /// </summary>
    internal async Task<IntegrationResult<TelephonyPortUse>> ResolvePortAsync(
        Guid tenantId, IntegrationCapability capability, CancellationToken ct)
    {
        var resolved = await ResolveAsync(tenantId, ct);

        if (resolved.IsFailure)
            return IntegrationResult.Functional<TelephonyPortUse>(resolved.Code!, resolved.Detail);

        var port = resolver.ResolvePort<ITelephonyPort>(resolved.Value, capability);

        return port.IsFailure
            ? IntegrationResult.Technical<TelephonyPortUse>(port.Code!, port.Detail)
            : IntegrationResult.Ok(new TelephonyPortUse(resolved.Value.Connection, port.Value));
    }
}

/// <summary>
/// A connection and the port to talk to it with. Paired for the reason <c>ResolvedAdapter</c>
/// gives: a port with no connection is a port whose settings and id a caller has to find again,
/// and the id is what every row of this family is keyed on.
/// </summary>
internal sealed record TelephonyPortUse(IntegrationConnection Connection, ITelephonyPort Port);
