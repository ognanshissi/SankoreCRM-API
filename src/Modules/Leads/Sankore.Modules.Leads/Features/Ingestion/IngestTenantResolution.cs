namespace Sankore.Modules.Leads.Features.Ingestion;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel.Models;

/// <summary>
/// Establishes the tenant of a public ingest request from the public key in its route.
///
/// <para>
/// <b>Why this is needed.</b> These routes are exempt from
/// <c>TenantResolutionMiddleware</c> — they have no JWT and no recognised domain, which is the
/// point of a public key. But "no ambient tenant" is not the same as "no tenant needed": the
/// commands they send implement <c>ICommand</c>, so <c>AuditBehavior</c> runs and reads
/// <c>ITenantContext.CurrentTenantId</c>, which throws
/// <c>InvalidOperationException: No tenant resolved for the current context</c> — after the lead
/// was already ingested, on the audit write. Every tenant-scoped query filter in
/// <c>LeadsDbContext</c> reads the same property, which is why the endpoints themselves are
/// written with <c>IgnoreQueryFilters()</c> throughout.
/// </para>
///
/// <para>
/// The public key identifies exactly one source and therefore exactly one tenant, so the request
/// CAN be scoped — it just has to be done before the pipeline asks. Writing
/// <c>HttpContext.Items[ResolvedTenantKey]</c> is how the middleware publishes its own answer, so
/// everything downstream (audit, query filters, nested commands) then behaves like any
/// tenant-scoped request rather than each handler threading a tenant by hand.
/// </para>
///
/// <para>
/// An unknown key short-circuits with 404, which is what all five endpoints already answer for
/// one — so the behaviour is unchanged and an unresolvable request never reaches a pipeline that
/// would fail on the audit row instead.
/// </para>
/// </summary>
internal static class IngestTenantResolution
{
    /// <summary>
    /// Applied to the GROUP in <c>LeadsModule.MapPublicIngestEndpoints</c> rather than to each
    /// route, so a public ingest route added later cannot be left without it — the symptom is a
    /// 500 on the audit write, long after the request looked successful.
    /// </summary>
    internal static TBuilder ResolveIngestTenantFromPublicKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(
            (ctx, next) => ResolveAsync(ctx.HttpContext, () => next(ctx)));

    /// <summary>
    /// The resolution itself, separated from the filter plumbing so it can be exercised without
    /// a server. <paramref name="next"/> is the rest of the pipeline.
    /// </summary>
    internal static async ValueTask<object?> ResolveAsync(
        HttpContext http, Func<ValueTask<object?>> next)
    {
        if (http.Request.RouteValues["publicKey"] is not string publicKey
            || publicKey.Length == 0)
            return Results.NotFound();

        // From RequestServices, not a constructor: an endpoint filter instance is built once for
        // the application, and a DbContext captured there would be a scoped service held by a
        // singleton — disposed, and shared across tenants.
        var db = http.RequestServices.GetRequiredService<LeadsDbContext>();

        var tenantId = await db.LeadSourceConfigs
            .IgnoreQueryFilters()
            .Where(s => s.PublicKey == publicKey)
            .Select(s => s.TenantId)
            .FirstOrDefaultAsync(http.RequestAborted);

        if (tenantId == Guid.Empty)
            return Results.NotFound();

        http.Items[TenantKey.ResolvedTenantKey] = tenantId;

        return await next();
    }
}
