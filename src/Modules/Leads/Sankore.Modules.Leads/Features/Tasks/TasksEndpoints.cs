namespace Sankore.Modules.Leads.Features.Tasks;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Tasks.AssignTask;
using Sankore.Modules.Leads.Features.Tasks.CancelTask;
using Sankore.Modules.Leads.Features.Tasks.CompleteTask;
using Sankore.Modules.Leads.Features.Tasks.CreateTask;
using Sankore.Modules.Leads.Features.Tasks.DispatchTask;
using Sankore.Modules.Leads.Features.Tasks.GetTask;
using Sankore.Modules.Leads.Features.Tasks.ListTasks;
using Sankore.Modules.Leads.Features.Tasks.ReassignTask;
using Sankore.Modules.Leads.Features.Tasks.StartTask;
using Sankore.Shared.Kernel;

public static class TasksEndpoints
{
    public static IEndpointRouteBuilder MapTasksEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("tasks");

        // GET /tasks
        group.MapGet("", ListTasks)
            .WithName("ListCrmTasks")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanReadCrmTasks.Code)
            .Produces<IReadOnlyList<CrmTaskDto>>()
            .WithOpenApi();

        // POST /tasks
        group.MapPost("", CreateTask)
            .WithName("CreateCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // GET /tasks/{taskId}
        group.MapGet("{taskId:guid}", GetTask)
            .WithName("GetCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanReadCrmTasks.Code)
            .Produces<CrmTaskDto>()
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST /tasks/{taskId}/complete
        group.MapPost("{taskId:guid}/complete", CompleteTask)
            .WithName("CompleteCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /tasks/{taskId}/cancel
        group.MapPost("{taskId:guid}/cancel", CancelTask)
            .WithName("CancelCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /tasks/{taskId}/start
        group.MapPost("{taskId:guid}/start", StartTask)
            .WithName("StartCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT /tasks/{taskId}/assign
        group.MapPut("{taskId:guid}/assign", AssignTask)
            .WithName("AssignCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // PUT /tasks/{taskId}/reassign  (US-M13-083 — audited reassignment)
        group.MapPut("{taskId:guid}/reassign", ReassignTask)
            .WithName("ReassignCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST /tasks/{taskId}/dispatch  (US-M13-081 — scored auto-dispatch)
        group.MapPost("{taskId:guid}/dispatch", DispatchTask)
            .WithName("DispatchCrmTask")
            .WithTags("Tasks")
            .RequireAuthorization(Permissions.CanManageCrmTasks.Code)
            .Produces<DispatchTaskResult>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListTasks(
        ISender sender, CancellationToken ct,
        Guid? leadId = null, Guid? agentId = null,
        CrmTaskStatus? status = null, CrmTaskType? type = null)
    {
        var result = await sender.Send(new ListTasksQuery(leadId, agentId, status, type), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateTask(
        CreateTaskRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CreateTaskCommand(
            req.TenantId, req.Type, req.Priority, req.Title, req.DueAt,
            req.LeadId, req.AssignedAgentId, req.SlaDeadline, req.Description), ct);

        return result.IsSuccess
            ? Results.Created($"tasks/{result.Value}", result.Value)
            : Results.Problem(title: "Create task failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> GetTask(Guid taskId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetTaskQuery(taskId), ct);
        return result.IsSuccess ? Results.Ok(result.Value) : Results.NotFound();
    }

    private static async Task<IResult> CompleteTask(Guid taskId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CompleteTaskCommand(taskId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TASK_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Complete failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> CancelTask(Guid taskId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new CancelTaskCommand(taskId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TASK_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Cancel failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> StartTask(Guid taskId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new StartTaskCommand(taskId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TASK_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Start failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> AssignTask(
        Guid taskId, AssignTaskRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new AssignTaskCommand(taskId, req.AgentId), ct);
        return result.IsSuccess
            ? Results.NoContent()
            : Results.NotFound();
    }

    private static async Task<IResult> DispatchTask(
        Guid taskId, DispatchTaskRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new DispatchTaskCommand(taskId, req.TenantId, req.Strategy), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : result.Error is "TASK_NOT_FOUND" or "LEAD_NOT_FOUND"
                ? Results.NotFound(new { error = result.Error })
                : Results.Problem(title: "Dispatch failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ReassignTask(
        Guid taskId, ReassignTaskRequest req, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(
            new ReassignTaskCommand(taskId, req.NewAgentId, req.Reason, req.ActorId, req.SlaExtensionHours), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TASK_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Reassign failed", detail: result.Error, statusCode: 422);
    }
}

public sealed record CreateTaskRequest(
    Guid TenantId,
    CrmTaskType Type,
    CrmTaskPriority Priority,
    string Title,
    DateTimeOffset DueAt,
    Guid? LeadId = null,
    Guid? AssignedAgentId = null,
    DateTimeOffset? SlaDeadline = null,
    string? Description = null);

public sealed record AssignTaskRequest(Guid AgentId);

public sealed record DispatchTaskRequest(
    Guid TenantId,
    DispatchingStrategy Strategy = DispatchingStrategy.CompatibilityScoring);

public sealed record ReassignTaskRequest(
    Guid NewAgentId,
    string Reason,
    Guid? ActorId = null,
    int? SlaExtensionHours = null);
