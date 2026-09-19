namespace Sankore.Modules.Leads.Features.DispatchingRules;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.DispatchingRules.ActivateDispatchingRule;
using Sankore.Modules.Leads.Features.DispatchingRules.CreateDispatchingRule;
using Sankore.Modules.Leads.Features.DispatchingRules.DeactivateDispatchingRule;
using Sankore.Modules.Leads.Features.DispatchingRules.GetDispatchingRule;
using Sankore.Modules.Leads.Features.DispatchingRules.ListDispatchingRules;
using Sankore.Modules.Leads.Features.DispatchingRules.SimulateDispatch;
using Sankore.Modules.Leads.Features.DispatchingRules.UpdateDispatchingRule;
using Sankore.Shared.Kernel;

public static class DispatchingRulesEndpoints
{
    public static IEndpointRouteBuilder MapDispatchingRulesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("dispatching-rules");

        // GET dispatching-rules
        group.MapGet("", ListRules)
            .WithName("ListDispatchingRules")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanReadDispatchingRules.Code)
            .Produces<IReadOnlyList<DispatchingRuleDto>>()
            .WithOpenApi();

        // GET dispatching-rules/{id}
        group.MapGet("{ruleId:guid}", GetRule)
            .WithName("GetDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanReadDispatchingRules.Code)
            .Produces<DispatchingRuleDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST dispatching-rules
        group.MapPost("", CreateRule)
            .WithName("CreateDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT dispatching-rules/{id}
        group.MapPut("{ruleId:guid}", UpdateRule)
            .WithName("UpdateDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST dispatching-rules/{id}/activate
        group.MapPost("{ruleId:guid}/activate", ActivateRule)
            .WithName("ActivateDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST dispatching-rules/{id}/deactivate
        group.MapPost("{ruleId:guid}/deactivate", DeactivateRule)
            .WithName("DeactivateDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST dispatching-rules/{id}/simulate
        group.MapPost("{ruleId:guid}/simulate", SimulateRule)
            .WithName("SimulateDispatchingRule")
            .WithTags("Dispatching Rules")
            .RequireAuthorization(Permissions.CanManageDispatchingRules.Code)
            .Produces<SimulateDispatchResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListRules(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListDispatchingRulesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> GetRule(
        Guid ruleId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetDispatchingRuleQuery(ruleId), ct);
        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> CreateRule(
        CreateDispatchingRuleRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateDispatchingRuleCommand(
            TenantId:              tenant.CurrentTenantId,
            Name:                  req.Name,
            Strategy:              req.Strategy,
            Weights:               req.Weights,
            MaxLeadsPerAgent:      req.MaxLeadsPerAgent,
            AntiMonopolyThreshold: req.AntiMonopolyThreshold,
            FirstContactSla:       req.FirstContactSla,
            Priority:              req.Priority,
            ExcludedAgentIds:      req.ExcludedAgentIds), ct);

        return result.IsSuccess
            ? Results.Created($"leads/dispatching-rules/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateRule(
        Guid ruleId,
        UpdateDispatchingRuleRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateDispatchingRuleCommand(
            RuleId:                ruleId,
            Name:                  req.Name,
            Weights:               req.Weights,
            MaxLeadsPerAgent:      req.MaxLeadsPerAgent,
            AntiMonopolyThreshold: req.AntiMonopolyThreshold,
            FirstContactSla:       req.FirstContactSla,
            Priority:              req.Priority,
            ExcludedAgentIds:      req.ExcludedAgentIds), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "RULE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateRule(
        Guid ruleId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateDispatchingRuleCommand(ruleId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateRule(
        Guid ruleId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateDispatchingRuleCommand(ruleId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SimulateRule(
        Guid ruleId,
        SimulateDispatchRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new SimulateDispatchQuery(ruleId, req.LeadId), ct);

        if (!result.IsSuccess)
        {
            return result.Error is "RULE_NOT_FOUND" or "LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Simulation failed", detail: result.Error, statusCode: 422);
        }

        return Results.Ok(result.Value);
    }
}

public sealed record CreateDispatchingRuleRequest(
    string Name,
    DispatchingStrategy Strategy,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    int Priority = 0,
    IReadOnlyList<Guid>? ExcludedAgentIds = null);

public sealed record UpdateDispatchingRuleRequest(
    string Name,
    ScoringWeightsDto Weights,
    int MaxLeadsPerAgent,
    int AntiMonopolyThreshold,
    TimeSpan FirstContactSla,
    int Priority = 0,
    IReadOnlyList<Guid>? ExcludedAgentIds = null);

public sealed record SimulateDispatchRequest(Guid LeadId);
