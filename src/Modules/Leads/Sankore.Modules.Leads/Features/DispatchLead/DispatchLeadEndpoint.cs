namespace Sankore.Modules.Leads.Features.DispatchLead;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// HTTP facade for this slice. Deliberately trivial: it reads the tenant
/// from the JWT, builds the command, sends it through MediatR, and maps
/// the Result to an HTTP response. All business logic lives in the
/// handler — this file should never grow beyond a few lines.
/// </summary>
public static class DispatchLeadEndpoint
{
    public static IEndpointRouteBuilder MapDispatchLead(this IEndpointRouteBuilder app)
    {
        app.MapPost("{leadId:guid}/dispatch", Handle)
            .WithName("DispatchLead")
            .WithTags("Leads")
            // "Leads.Dispatch" was not a policy. AddSankoreAuthorization registers exactly one
            // policy per Permissions.All entry, named after permission.Code ("lead:..."), and
            // there is no IAuthorizationPolicyProvider to resolve anything else — so this name,
            // the only non-conforming one left in the module, made the endpoint throw
            // "The AuthorizationPolicy named: 'Leads.Dispatch' was not found" on every
            // authenticated call. Manual dispatch was therefore impossible, which is why a lead
            // whose auto-dispatch failed could only be given an owner by hand — and an owner is
            // not an assignment, so RecordFirstContact kept refusing it.
            .RequireAuthorization(Permissions.CanAssignLead.Code)
            .Produces<DispatchLeadResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> Handle(
        Guid leadId,
        DispatchLeadRequest req,
        ISender sender,
        HttpContext http,
        CancellationToken ct)
    {
        var tenantId = http.User.GetTenantId();

        var result = await sender.Send(
            new DispatchLeadCommand(leadId, tenantId, req.Strategy, req.AgentId, req.OverrideReason), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.Problem(
                title: "Dispatching failed",
                detail: result.Error,
                statusCode: StatusCodes.Status422UnprocessableEntity);
    }
}

/// <param name="Strategy">
/// Omit it to let the applicable dispatching rule decide; name one to force it. Cannot be
/// combined with <paramref name="AgentId"/>.
/// </param>
/// <param name="AgentId">
/// Assign to this agent instead of ranking candidates — the supervisor override. The agent must
/// still be available for the lead's agency and must not be on the rule's exclusion list;
/// task-capacity and anti-monopoly limits are not applied. Requires
/// <paramref name="OverrideReason"/>.
/// </param>
/// <param name="OverrideReason">Why this agent was chosen by hand. Required with AgentId.</param>
public sealed record DispatchLeadRequest(
    DispatchingStrategy? Strategy = null,
    Guid? AgentId = null,
    string? OverrideReason = null);
