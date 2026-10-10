namespace Sankore.Modules.Integration.Features.CallLog;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.CallLog.GetCallLogStats;
using Sankore.Modules.Integration.Features.CallLog.ListCallLog;

/// <summary>
/// Area aggregator: one MapGroup, one call per slice. Adding a feature touches its own folder
/// plus a single line here — the repo's vertical-slice rule.
///
/// <para>
/// Both slices are reads behind <c>Integration.Command.View</c>, the permission that already
/// governs the commands and the rejection queue. The journal is the same operational story seen
/// at call granularity, so giving it a permission of its own would mean an operator who can read
/// why a command was rejected but not which calls produced the rejection.
/// </para>
/// </summary>
internal static class CallLogEndpoints
{
    internal static IEndpointRouteBuilder MapCallLogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("integration/call-log").WithTags("Integration");

        group.MapListCallLog();
        group.MapGetCallLogStats();

        return app;
    }
}
