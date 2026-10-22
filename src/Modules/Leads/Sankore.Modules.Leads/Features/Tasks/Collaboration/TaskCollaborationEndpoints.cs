namespace Sankore.Modules.Leads.Features.Tasks.Collaboration;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Shared.Kernel;

/// <summary>
/// The task page: detail, edits, associated colleagues, criteria, comments, tags and files —
/// under <c>api/v1/leads/tasks/{taskId}</c>. Reading needs <c>lead:task:read</c>, writing
/// <c>lead:task:manage</c>, and EVERY route also goes through <see cref="TaskAccess"/>: outside
/// the task's people and the ones who answer for its holder, a task does not exist (404).
/// </summary>
internal static class TaskCollaborationEndpoints
{
    internal static RouteGroupBuilder MapTaskCollaborationEndpoints(this RouteGroupBuilder group)
    {
        var read = Permissions.CanReadCrmTasks.Code;
        var write = Permissions.CanManageCrmTasks.Code;

        group.MapGet("{taskId:guid}/detail", async (Guid taskId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new GetTaskDetailQuery(taskId), ct)))
            .WithName("GetCrmTaskDetail").WithTags("Tasks")
            .WithSummary("One task with its subject, people, criteria, tags and files")
            .RequireAuthorization(read)
            .Produces<CrmTaskDetailDto>().Produces(StatusCodes.Status404NotFound).WithOpenApi();

        group.MapPatch("{taskId:guid}", async (Guid taskId, UpdateTaskDetailsRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new UpdateTaskDetailsCommand(taskId, req.Title, req.Description, req.Priority, req.DueAt), ct)))
            .WithName("UpdateCrmTaskDetails").WithTags("Tasks")
            .WithSummary("Title, description, priority and due date of an open task")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPut("{taskId:guid}/participants", async (Guid taskId, SetTaskParticipantsRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetTaskParticipantsCommand(taskId, req.UserIds ?? []), ct)))
            .WithName("SetCrmTaskParticipants").WithTags("Tasks")
            .WithSummary("The colleagues associated with the task, as a whole set (20 at most); the added ones are notified")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPost("{taskId:guid}/criteria", async (Guid taskId, AddTaskCriterionRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new AddTaskCriterionCommand(taskId, req.Label), ct)))
            .WithName("AddCrmTaskCriterion").WithTags("Tasks")
            .RequireAuthorization(write)
            .Produces<Guid>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPatch("{taskId:guid}/criteria/{criterionId:guid}",
                async (Guid taskId, Guid criterionId, UpdateTaskCriterionRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new UpdateTaskCriterionCommand(taskId, criterionId, req.Label, req.IsMet), ct)))
            .WithName("UpdateCrmTaskCriterion").WithTags("Tasks")
            .WithSummary("Renames a criterion, or ticks / unticks it (who and when are recorded)")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapDelete("{taskId:guid}/criteria/{criterionId:guid}", async (Guid taskId, Guid criterionId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RemoveTaskCriterionCommand(taskId, criterionId), ct)))
            .WithName("RemoveCrmTaskCriterion").WithTags("Tasks")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapGet("{taskId:guid}/comments", async (Guid taskId, DateTimeOffset? before, int? limit, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new ListTaskCommentsQuery(taskId, before, limit ?? 50), ct)))
            .WithName("ListCrmTaskComments").WithTags("Tasks")
            .WithSummary("The thread, oldest first within a page; `before` pages towards older comments")
            .RequireAuthorization(read)
            .Produces<TaskCommentsPageDto>().Produces(StatusCodes.Status404NotFound).WithOpenApi();

        group.MapPost("{taskId:guid}/comments", async (Guid taskId, TaskCommentRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new AddTaskCommentCommand(taskId, req.Body), ct)))
            .WithName("AddCrmTaskComment").WithTags("Tasks")
            .RequireAuthorization(write)
            .Produces<Guid>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPatch("{taskId:guid}/comments/{commentId:guid}",
                async (Guid taskId, Guid commentId, TaskCommentRequest req, ISender sender, CancellationToken ct) =>
                    Map(await sender.Send(new EditTaskCommentCommand(taskId, commentId, req.Body), ct)))
            .WithName("EditCrmTaskComment").WithTags("Tasks")
            .WithSummary("Its author edits a comment")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapDelete("{taskId:guid}/comments/{commentId:guid}", async (Guid taskId, Guid commentId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new DeleteTaskCommentCommand(taskId, commentId), ct)))
            .WithName("DeleteCrmTaskComment").WithTags("Tasks")
            .WithSummary("Its author deletes a comment; the thread keeps « commentaire supprimé »")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPut("{taskId:guid}/tags", async (Guid taskId, SetTaskTagsRequest req, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new SetTaskTagsCommand(taskId, req.Tags ?? []), ct)))
            .WithName("SetCrmTaskTags").WithTags("Tasks")
            .WithSummary("The task's tags as a whole set; answers them normalised")
            .RequireAuthorization(write)
            .Produces<IReadOnlyList<string>>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapPost("{taskId:guid}/attachments", async (Guid taskId, IFormFile file, IOptions<TaskAttachmentStorageOptions> options, ISender sender, CancellationToken ct) =>
            {
                // Refused before reading: the size is known from the request, the bytes are not needed.
                if (file.Length > options.Value.MaxBytes) return Problem("TASK_ATTACHMENT_TOO_LARGE");

                using var buffer = new MemoryStream((int)file.Length);
                await file.CopyToAsync(buffer, ct);
                return Map(await sender.Send(new StoreTaskAttachment(taskId, file.FileName, buffer.ToArray()), ct));
            })
            .WithName("AddCrmTaskAttachment").WithTags("Tasks")
            .WithSummary("Attaches a file (PDF, image, Office document, text; 10 MB): stored encrypted")
            .RequireAuthorization(write)
            .Accepts<IFormFile>("multipart/form-data")
            .DisableAntiforgery()
            .Produces<TaskAttachmentDto>().Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        group.MapGet("{taskId:guid}/attachments/{attachmentId:guid}/content", async (Guid taskId, Guid attachmentId, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new GetTaskAttachmentContentQuery(taskId, attachmentId), ct);
                return result.IsSuccess
                    ? Results.File(result.Value!.Content, result.Value.ContentType, result.Value.FileName)
                    : Results.NotFound();
            })
            .WithName("DownloadCrmTaskAttachment").WithTags("Tasks")
            .RequireAuthorization(read)
            .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream").Produces(StatusCodes.Status404NotFound).WithOpenApi();

        group.MapDelete("{taskId:guid}/attachments/{attachmentId:guid}", async (Guid taskId, Guid attachmentId, ISender sender, CancellationToken ct) =>
                Map(await sender.Send(new RemoveTaskAttachmentCommand(taskId, attachmentId), ct)))
            .WithName("RemoveCrmTaskAttachment").WithTags("Tasks")
            .WithSummary("Removes a file: its uploader, the holder or the creator of the task")
            .RequireAuthorization(write)
            .Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).Produces(StatusCodes.Status422UnprocessableEntity).WithOpenApi();

        return group;
    }

    private static IResult Map<T>(Result<T> result) => result.IsSuccess ? Results.Ok(result.Value) : Problem(result.Error!);

    private static IResult Map(Result result) => result.IsSuccess ? Results.NoContent() : Problem(result.Error!);

    private static IResult Problem(string code) => code switch
    {
        TaskAccess.NotFound or "TASK_CRITERION_NOT_FOUND" or "TASK_COMMENT_NOT_FOUND" or "TASK_ATTACHMENT_NOT_FOUND" =>
            Results.Problem(title: "Tâche", detail: code, statusCode: StatusCodes.Status404NotFound),
        "TASK_ATTACHMENTS_UNAVAILABLE" => Results.Problem(title: "Tâche", detail: code, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Problem(title: "Tâche", detail: code, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
