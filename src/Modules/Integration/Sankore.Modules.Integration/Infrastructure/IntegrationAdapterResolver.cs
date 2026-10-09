namespace Sankore.Modules.Integration.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Shared.Kernel;

/// <summary>
/// Finds the adapter a tenant's calls must go through (INT-02).
///
/// <para>
/// Resolution is in two steps and both can fail for different reasons, which the caller needs to
/// tell apart: there may be no active connection (the IMF has not configured one — a
/// <c>Technical</c> failure naming the family), or there may be one whose kind has no registered
/// adapter (a deployment that shipped without the assembly). Collapsing the two into "no
/// adapter" sends an administrator looking in the wrong place.
/// </para>
/// </summary>
internal sealed class IntegrationAdapterResolver(
    IntegrationDbContext db,
    IServiceProvider services)
{
    /// <summary>
    /// The tenant's active connection for a family, or a functional failure naming what is
    /// missing. Takes the tenant explicitly: the dispatcher runs outside any HTTP request.
    /// </summary>
    public async Task<IntegrationResult<IntegrationConnection>> ResolveConnectionAsync(
        Guid tenantId, IntegrationFamily family, CancellationToken ct)
    {
        // IgnoreQueryFilters plus an explicit predicate: a Hangfire job has no ambient tenant,
        // and the one being processed is the argument, not the context.
        var connection = await db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId && c.Family == family && c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return connection is null
            ? IntegrationResult.Technical<IntegrationConnection>(
                IntegrationErrors.NoActiveConnection,
                $"No active {family} connection is configured for this tenant.")
            : IntegrationResult.Ok(connection);
    }

    /// <summary>One named connection, whatever its family — the insurance path (ASS-02).</summary>
    public async Task<IntegrationResult<IntegrationConnection>> ResolveConnectionAsync(
        Guid tenantId, Guid connectionId, CancellationToken ct)
    {
        var connection = await db.Connections
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == connectionId, ct);

        return connection is null
            ? IntegrationResult.Technical<IntegrationConnection>(
                IntegrationErrors.ConnectionNotFound,
                "The connection does not exist for this tenant.")
            : IntegrationResult.Ok(connection);
    }

    /// <summary>
    /// The adapter registered for a connection's kind. Keyed DI: one registration per
    /// <see cref="IntegrationKind"/>, so adding a CBS adds an assembly and a line, never a switch
    /// in a caller.
    /// </summary>
    public IntegrationResult<ICbsAdapter> ResolveAdapter(IntegrationConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var adapter = services.GetKeyedService<ICbsAdapter>(connection.Kind.ToString());

        return adapter is null
            ? IntegrationResult.Technical<ICbsAdapter>(
                IntegrationErrors.AdapterNotRegistered,
                $"No adapter is registered for kind {connection.Kind}.")
            : IntegrationResult.Ok(adapter);
    }

    /// <summary>
    /// Both steps at once, for the common case. Returns the connection alongside the adapter
    /// because every adapter method needs the settings that connection carries.
    /// </summary>
    public async Task<IntegrationResult<ResolvedAdapter>> ResolveAsync(
        Guid tenantId, IntegrationFamily family, CancellationToken ct)
    {
        var connection = await ResolveConnectionAsync(tenantId, family, ct);
        if (connection.IsFailure)
            return IntegrationResult.Technical<ResolvedAdapter>(connection.Code!, connection.Detail);

        var adapter = ResolveAdapter(connection.Value);
        if (adapter.IsFailure)
            return IntegrationResult.Technical<ResolvedAdapter>(adapter.Code!, adapter.Detail);

        return IntegrationResult.Ok(new ResolvedAdapter(connection.Value, adapter.Value));
    }

    /// <summary>A port of the resolved adapter, or a clear statement that it cannot do this.</summary>
    public IntegrationResult<TPort> ResolvePort<TPort>(
        ICbsAdapter adapter, IntegrationCapability capability) where TPort : class
    {
        ArgumentNullException.ThrowIfNull(adapter);

        if (!adapter.Capabilities.Supports(capability))
            return IntegrationResult.Technical<TPort>(
                IntegrationErrors.CapabilityNotSupported,
                $"{adapter.Kind} does not support {capability}.");

        return adapter is TPort port
            ? IntegrationResult.Ok(port)
            : IntegrationResult.Technical<TPort>(
                IntegrationErrors.CapabilityNotSupported,
                $"{adapter.Kind} declares {capability} but does not implement {typeof(TPort).Name}.");
    }
}

/// <summary>A connection and the adapter that serves it.</summary>
internal sealed record ResolvedAdapter(IntegrationConnection Connection, ICbsAdapter Adapter);
