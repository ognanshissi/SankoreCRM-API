namespace Sankore.Modules.Integration.Features.References;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.References.GetReference;

/// <summary>
/// Area aggregator for the identifier correspondence (INT-07). One <c>MapGroup</c>, one call per
/// slice; adding a feature touches its own folder plus a single line here.
///
/// <para>
/// Its own area rather than a route under <c>integration/commands</c>: a reference outlives every
/// command that produced it, and the reconciliation reads it without going anywhere near the
/// command queue.
/// </para>
/// </summary>
internal static class ReferencesEndpoints
{
    internal static IEndpointRouteBuilder MapIntegrationReferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/references").WithTags("Integration");

        group.MapGetIntegrationReference();

        return app;
    }
}
