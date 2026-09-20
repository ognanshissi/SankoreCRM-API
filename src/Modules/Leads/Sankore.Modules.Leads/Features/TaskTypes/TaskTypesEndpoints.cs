namespace Sankore.Modules.Leads.Features.TaskTypes;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Leads.Features.TaskTypes.ActivateTaskType;
using Sankore.Modules.Leads.Features.TaskTypes.CreateTaskType;
using Sankore.Modules.Leads.Features.TaskTypes.DeactivateTaskType;
using Sankore.Modules.Leads.Features.TaskTypes.ListTaskTypes;
using Sankore.Modules.Leads.Features.TaskTypes.UpdateTaskType;
using Sankore.Shared.Kernel;

public static class TaskTypesEndpoints
{
    public static IEndpointRouteBuilder MapTaskTypesEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("task-types");

        // GET task-types
        group.MapGet("", ListTaskTypes)
            .WithName("ListTaskTypes")
            .WithTags("Task Types")
            .RequireAuthorization(Permissions.CanReadTaskTypes.Code)
            .Produces<IReadOnlyList<TaskTypeDto>>()
            .WithOpenApi();

        // POST task-types
        group.MapPost("", CreateTaskType)
            .WithName("CreateTaskType")
            .WithTags("Task Types")
            .RequireAuthorization(Permissions.CanManageTaskTypes.Code)
            .Produces<Guid>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // PUT task-types/{id}
        group.MapPut("{taskTypeId:guid}", UpdateTaskType)
            .WithName("UpdateTaskType")
            .WithTags("Task Types")
            .RequireAuthorization(Permissions.CanManageTaskTypes.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        // POST task-types/{id}/activate
        group.MapPost("{taskTypeId:guid}/activate", ActivateTaskType)
            .WithName("ActivateTaskType")
            .WithTags("Task Types")
            .RequireAuthorization(Permissions.CanManageTaskTypes.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi();

        // POST task-types/{id}/deactivate
        group.MapPost("{taskTypeId:guid}/deactivate", DeactivateTaskType)
            .WithName("DeactivateTaskType")
            .WithTags("Task Types")
            .RequireAuthorization(Permissions.CanManageTaskTypes.Code)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status422UnprocessableEntity)
            .WithOpenApi();

        return app;
    }

    private static async Task<IResult> ListTaskTypes(
        ISender sender, CancellationToken ct, bool? activeOnly = null)
    {
        var result = await sender.Send(new ListTaskTypesQuery(activeOnly), ct);
        return Results.Ok(result.Value);
    }

    private static async Task<IResult> CreateTaskType(
        CreateTaskTypeRequest req,
        ISender sender,
        ITenantContext tenant,
        CancellationToken ct)
    {
        var result = await sender.Send(new CreateTaskTypeCommand(
            TenantId:     tenant.CurrentTenantId,
            Code:         req.Code,
            Label:        req.Label,
            Description:  req.Description,
            DisplayOrder: req.DisplayOrder), ct);

        return result.IsSuccess
            ? Results.Created($"task-types/{result.Value}", result.Value)
            : Results.Problem(title: "Create failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> UpdateTaskType(
        Guid taskTypeId,
        UpdateTaskTypeRequest req,
        ISender sender,
        CancellationToken ct)
    {
        var result = await sender.Send(new UpdateTaskTypeCommand(
            TaskTypeId:   taskTypeId,
            Label:        req.Label,
            Description:  req.Description,
            DisplayOrder: req.DisplayOrder), ct);

        return result.IsSuccess
            ? Results.NoContent()
            : result.Error == "TASK_TYPE_NOT_FOUND"
                ? Results.NotFound()
                : Results.Problem(title: "Update failed", detail: result.Error, statusCode: 422);
    }

    private static async Task<IResult> ActivateTaskType(
        Guid taskTypeId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new ActivateTaskTypeCommand(taskTypeId), ct);
        return result.IsSuccess ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeactivateTaskType(
        Guid taskTypeId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new DeactivateTaskTypeCommand(taskTypeId), ct);

        if (result.IsSuccess)
            return Results.NoContent();

        return result.Error switch
        {
            "TASK_TYPE_NOT_FOUND"              => Results.NotFound(),
            "CANNOT_DEACTIVATE_SYSTEM_TYPE"    => Results.Problem(
                title: "Deactivation rejected",
                detail: result.Error,
                statusCode: 422),
            _ => Results.Problem(title: "Deactivation failed", detail: result.Error, statusCode: 422)
        };
    }
}

public sealed record CreateTaskTypeRequest(
    string Code,
    string Label,
    string? Description = null,
    int DisplayOrder = 0);

public sealed record UpdateTaskTypeRequest(
    string Label,
    string? Description = null,
    int DisplayOrder = 0);
