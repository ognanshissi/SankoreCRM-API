namespace Sankore.Modules.Integration.Features.Reconciliation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Reconciliation.ListGaps;
using Sankore.Modules.Integration.Features.Reconciliation.ResolveGap;

/// <summary>
/// Area aggregator: one <c>MapGroup</c>, one call per slice. Adding a feature touches its own
/// folder plus a single line here — the repo's vertical-slice rule.
///
/// <para>
/// Two different permissions, deliberately: reading the ledger is
/// <c>Integration.Reconciliation.View</c> (which <c>RoleSeeder</c> already grants to
/// <c>RegulationManager</c>, the internal-control role this story is written for) and closing a
/// finding is <c>Integration.Reconciliation.Resolve</c>. An officer who may audit the comparison
/// is not thereby entitled to sign off its findings.
/// </para>
///
/// <para>
/// <b>There is deliberately no endpoint that TRIGGERS a comparison.</b> It is a daily job on a
/// global schedule; an on-demand run would let a caller start a full-portfolio sweep per request,
/// and it would also produce a run row inside working hours whose gaps an operator closes a minute
/// later — which is how a compliance ledger stops being believed. An operator who needs one now
/// resumes or triggers the recurring job from the Hangfire dashboard's Job control page.
/// </para>
///
/// <para>
/// The group is <c>integration/reconciliation</c> rather than
/// <c>integration/reconciliation/gaps</c> so that the runs log — the other half of what
/// <c>Integration.Reconciliation.View</c> covers, and today surfaced only as the
/// <c>lastRun</c> of the gaps page — can be added beside it without moving a published route.
/// </para>
/// </summary>
internal static class ReconciliationEndpoints
{
    internal static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/reconciliation").WithTags("Integration");

        group.MapListReconciliationGaps();
        group.MapResolveReconciliationGap();

        return app;
    }
}
