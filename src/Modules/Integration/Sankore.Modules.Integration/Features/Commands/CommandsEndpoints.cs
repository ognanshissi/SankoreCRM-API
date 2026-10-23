namespace Sankore.Modules.Integration.Features.Commands;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Commands.CancelCommand;
using Sankore.Modules.Integration.Features.Commands.GetCommand;
using Sankore.Modules.Integration.Features.Commands.ListCommands;
using Sankore.Modules.Integration.Features.Commands.ReplayCommand;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

/// <summary>
/// Area aggregator: one <c>MapGroup</c>, one call per slice. Adding a feature touches its own
/// folder plus a single line here — the repo's vertical-slice rule.
///
/// <para>
/// There is deliberately no endpoint that EXECUTES a command. A human replays or cancels; the
/// dispatcher of INT-06 decides when a replayed command runs, because it is the only thing that
/// knows about the per-customer ordering and the attempt budget.
/// </para>
/// </summary>
internal static class CommandsEndpoints
{
    internal static IEndpointRouteBuilder MapIntegrationCommandsEndpoints(this IEndpointRouteBuilder app)
    {
        // Commands go to a core banking system or an insurer: open with either paid module.
        var group = app.MapGroup("integration/commands")
            .WithTags("Integration")
            .RequireTenantModule(TenantModules.BankingMicrofinance, TenantModules.Insurance);

        group.MapListIntegrationCommands();
        group.MapGetIntegrationCommand();
        group.MapReplayIntegrationCommand();
        group.MapCancelIntegrationCommand();

        return app;
    }
}
