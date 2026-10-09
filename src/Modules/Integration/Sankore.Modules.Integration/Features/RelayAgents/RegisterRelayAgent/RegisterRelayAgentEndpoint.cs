namespace Sankore.Modules.Integration.Features.RelayAgents.RegisterRelayAgent;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// Registers an agent and hands back its single-use enrolment token.
///
/// <para>
/// The only route in this module that returns a credential, and it returns it exactly once. There
/// is deliberately no companion GET: the hash is all that is stored, so nothing here could show
/// the token again even if a route asked for it — and a route that asked would turn an
/// administration screen into a credential store with an audit row attached.
/// </para>
/// </summary>
internal static class RegisterRelayAgentEndpoint
{
    internal static IEndpointRouteBuilder MapRegisterRelayAgent(this IEndpointRouteBuilder app)
    {
        app.MapPost(string.Empty, Handle)
            .WithName("RegisterIntegrationRelayAgent")
            .WithSummary("Register an on-premise relay agent and mint its enrolment token")
            .WithDescription(
                "Creates a relay agent for the CURRENT tenant and arms a single-use enrolment "
                + "token, valid 30 minutes, which the agent exchanges for its client certificate "
                + "through the public enrolment endpoint. The token is returned by THIS call and "
                + "never again: only its SHA-256 hash is stored, so no endpoint can show it a "
                + "second time — an operator who loses it registers another agent and revokes "
                + "this one. The agent's tenant comes from the token presented at exchange time "
                + "and is never accepted in a request body, which is what stops one institution "
                + "routing its writes through another's on-premise network. Audited. "
                + "Requires permission: Integration.Connection.Manage.")
            .RequireAuthorization(Permissions.CanManageIntegrationConnection.Code)
            .Produces<RelayAgentEnrolmentDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        RegisterRelayAgentRequest req,
        ISender sender,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var result = await sender.Send(new RegisterRelayAgentCommand(req.Name), ct);

        // 201 with no Location header: there is no single-agent GET to point at, and a Location
        // naming a route that does not exist is worse than none. Results.Json rather than
        // Results.Created for exactly that reason.
        return result.IsSuccess
            ? Results.Json(result.Value, statusCode: StatusCodes.Status201Created)
            : Results.BadRequest(new { error = result.Error });
    }
}

/// <summary>
/// A name, and nothing more. No agent id, no tenant id, no connection id — see
/// <see cref="RegisterRelayAgentCommand"/> and docs/integration-module-plan.md §5bis (a) for why
/// an identifier accepted here would be a cross-tenant leak in both directions.
/// </summary>
internal sealed record RegisterRelayAgentRequest(string Name);
