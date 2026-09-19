namespace Sankore.Modules.Leads.Features.TaskGenerationRules;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.TaskGenerationRules.ActivateTaskGenerationRule;
using Sankore.Modules.Leads.Features.TaskGenerationRules.CreateTaskGenerationRule;
using Sankore.Modules.Leads.Features.TaskGenerationRules.DeactivateTaskGenerationRule;
using Sankore.Modules.Leads.Features.TaskGenerationRules.ListTaskGenerationRules;
using Sankore.Modules.Leads.Features.TaskGenerationRules.UpdateTaskGenerationRule;
using Sankore.Shared.Kernel;

public static class TaskGenerationRulesEndpoints
{
    public static IEndpointRouteBuilder MapTaskGenerationRulesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("task-generation-rules");

        // GET /task-generation-rules
        group.MapGet("", ListRules)
            .WithName("ListTaskGenerationRules")
            .WithTags("TaskGenerationRules")
            .RequireAuthorization(Permissions.CanManageTaskGenerationRules.Code)
            .Produces<IReadOnlyList<TaskGenerationRuleDto>>()
            .WithOpenApi();

        // POST /task-generation-rules
        group.MapPost("", CreateRule)
            .WithName("CreateTaskGenerationRule")
            .WithTags("TaskGenerationRules")
            .RequireAuthorization(Permissions.CanManageTaskGenerationRules.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT /task-generation-rules/{ruleId}
        group.MapPut("{ruleId:guid}", UpdateRule)
            .WithName("UpdateTaskGenerationRule")
            .WithTags("TaskGenerationRules")
            .RequireAuthorization(Permissions.CanManageTaskGenerationRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /task-generation-rules/{ruleId}/activate
        group.MapPost("{ruleId:guid}/activate", ActivateRule)
            .WithName("ActivateTaskGenerationRule")
            .WithTags("TaskGenerationRules")
            .RequireAuthorization(Permissions.CanManageTaskGenerationRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /task-generation-rules/{ruleId}/deactivate
        group.MapPost("{ruleId:guid}/deactivate", DeactivateRule)
            .WithName("DeactivateTaskGenerationRule")
            .WithTags("TaskGenerationRules")
            .RequireAuthorization(Permissions.CanManageTaskGenerationRules.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListRules(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListTaskGenerationRulesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateRule(
        CreateTaskGenerationRuleRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CreateTaskGenerationRuleCommand(
            req.TenantId, req.TriggerEventType, req.TaskType, req.Priority,
            req.TitleTemplate, req.SlaDuration, req.DueDuration, req.DescriptionTemplate), ct);

        return result.IsSuccess
            ? Results.Created($"task-generation-rules/{result.Value}", result.Value)
            : Results.Problem(title: "Create rule failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateRule(
        Guid ruleId, UpdateTaskGenerationRuleRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new UpdateTaskGenerationRuleCommand(
            ruleId, req.TriggerEventType, req.TaskType, req.Priority,
            req.TitleTemplate, req.SlaDuration, req.DueDuration, req.DescriptionTemplate), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "RULE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateRule(
        Guid ruleId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateTaskGenerationRuleCommand(ruleId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateRule(
        Guid ruleId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateTaskGenerationRuleCommand(ruleId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }
}

public sealed record CreateTaskGenerationRuleRequest(
    Guid TenantId,
    string TriggerEventType,
    CrmTaskType TaskType,
    CrmTaskPriority Priority,
    string TitleTemplate,
    TimeSpan SlaDuration,
    TimeSpan DueDuration,
    string? DescriptionTemplate = null);

public sealed record UpdateTaskGenerationRuleRequest(
    string TriggerEventType,
    CrmTaskType TaskType,
    CrmTaskPriority Priority,
    string TitleTemplate,
    TimeSpan SlaDuration,
    TimeSpan DueDuration,
    string? DescriptionTemplate = null);
