namespace Sankore.Modules.Leads.Features.Tasks.Collaboration;

using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Messaging;
using Sankore.Shared.Kernel;

// ── DTOs ─────────────────────────────────────────────────────────────────────────────────────

public sealed record TaskPersonDto(Guid Id, string Name);

public sealed record TaskCriterionDto(Guid Id, string Label, bool IsMet, Guid? MetBy, string? MetByName, DateTimeOffset? MetAt);

public sealed record TaskAttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, Guid UploadedBy, string UploadedByName, DateTimeOffset UploadedAt);

/// <param name="Body">Null once deleted — the thread shows « commentaire supprimé ».</param>
public sealed record TaskCommentDto(
    Guid Id, Guid AuthorId, string AuthorName, string? Body, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, bool IsDeleted, bool IsMine);

/// <param name="NextBefore">Pass it back as <c>before</c> for the older comments; null when there are none.</param>
public sealed record TaskCommentsPageDto(IReadOnlyList<TaskCommentDto> Items, DateTimeOffset? NextBefore);

/// <summary>
/// One task, everything its page shows except the comments (paged apart) and the assignment
/// history (<c>GET tasks/{id}/assignments</c>). Enums are strings.
/// </summary>
/// <param name="IsOpen">Neither completed nor cancelled: details, criteria and people can change.</param>
public sealed record CrmTaskDetailDto(
    Guid Id,
    string Type,
    string Priority,
    string Title,
    string? Description,
    string Status,
    DateTimeOffset DueAt,
    DateTimeOffset? SlaDeadline,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TaskSubjectDto? Subject,
    TaskPersonDto? Holder,
    TaskPersonDto? Creator,
    IReadOnlyList<TaskPersonDto> Participants,
    IReadOnlyList<TaskCriterionDto> Criteria,
    IReadOnlyList<string> Tags,
    IReadOnlyList<TaskAttachmentDto> Attachments,
    int CommentCount,
    bool IsOpen);

// ── Detail ───────────────────────────────────────────────────────────────────────────────────

internal sealed record GetTaskDetailQuery(Guid TaskId) : IRequest<Result<CrmTaskDetailDto>>;

internal sealed class GetTaskDetailHandler(LeadsDbContext db, TaskAccess access, TaskSubjects subjects, IAdministrationModule administration)
    : IRequestHandler<GetTaskDetailQuery, Result<CrmTaskDetailDto>>
{
    public async Task<Result<CrmTaskDetailDto>> Handle(GetTaskDetailQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<CrmTaskDetailDto>(loaded.Error!);
        var task = loaded.Value!;

        var participants = await db.CrmTaskParticipants.AsNoTracking().Where(p => p.TaskId == task.Id).OrderBy(p => p.AddedAt).ToListAsync(ct);
        var criteria = await db.CrmTaskCriteria.AsNoTracking().Where(c => c.TaskId == task.Id).OrderBy(c => c.Position).ThenBy(c => c.CreatedAt).ToListAsync(ct);
        var tags = await db.CrmTaskTags.AsNoTracking().Where(t => t.TaskId == task.Id).OrderBy(t => t.Tag).Select(t => t.Tag).ToListAsync(ct);
        var attachments = await db.CrmTaskAttachments.AsNoTracking().Where(a => a.TaskId == task.Id && a.DeletedAt == null).OrderBy(a => a.UploadedAt).ToListAsync(ct);
        var commentCount = await db.CrmTaskComments.CountAsync(c => c.TaskId == task.Id && c.DeletedAt == null, ct);

        var ids = new[] { task.AssignedAgentId, task.CreatedBy }.OfType<Guid>()
            .Concat(participants.Select(p => p.UserId))
            .Concat(criteria.Select(c => c.MetBy).OfType<Guid>())
            .Concat(attachments.Select(a => a.UploadedBy))
            .Distinct().ToList();
        var names = await TaskPeople.NamesAsync(administration, task.TenantId, ids, ct);

        return Result.Ok(new CrmTaskDetailDto(
            task.Id, task.Type.ToString(), task.Priority.ToString(), task.Title, task.Description, task.Status.ToString(),
            task.DueAt, task.SlaDeadline, task.CreatedAt, task.StartedAt, task.CompletedAt,
            await subjects.DescribeAsync(task, ct),
            TaskPeople.Person(task.AssignedAgentId, names),
            TaskPeople.Person(task.CreatedBy, names),
            participants.Select(p => TaskPeople.Person(p.UserId, names)!).ToList(),
            criteria.Select(c => new TaskCriterionDto(c.Id, c.Label, c.IsMet, c.MetBy, c.MetBy is { } m ? names.GetValueOrDefault(m) : null, c.MetAt)).ToList(),
            tags,
            attachments.Select(a => new TaskAttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.UploadedBy, names.GetValueOrDefault(a.UploadedBy) ?? "Utilisateur", a.UploadedAt)).ToList(),
            commentCount,
            !task.IsClosed));
    }
}

internal static class TaskPeople
{
    public static async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(
        IAdministrationModule administration, Guid tenantId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        using var suppressed = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        return await administration.GetUserNamesAsync(tenantId, ids, ct);
    }

    public static TaskPersonDto? Person(Guid? id, IReadOnlyDictionary<Guid, string> names) =>
        id is { } value && value != Guid.Empty ? new TaskPersonDto(value, names.GetValueOrDefault(value) ?? "Utilisateur") : null;
}

// ── Details ──────────────────────────────────────────────────────────────────────────────────

public sealed record UpdateTaskDetailsRequest(string Title, string? Description, CrmTaskPriority Priority, DateTimeOffset? DueAt);

internal sealed record UpdateTaskDetailsCommand(Guid TaskId, string Title, string? Description, CrmTaskPriority Priority, DateTimeOffset? DueAt)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed class UpdateTaskDetailsHandler(LeadsDbContext db, TaskAccess access, TimeProvider clock)
    : IRequestHandler<UpdateTaskDetailsCommand, Result>
{
    public async Task<Result> Handle(UpdateTaskDetailsCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: true, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);
        var task = loaded.Value!;

        var updated = task.UpdateDetails(cmd.Title, cmd.Description, cmd.Priority);
        if (updated.IsFailure) return updated;

        if (cmd.DueAt is { } due && due != task.DueAt)
        {
            var moved = task.Reschedule(due, clock.GetUtcNow());
            if (moved.IsFailure) return moved;
        }

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── People ───────────────────────────────────────────────────────────────────────────────────

public sealed record SetTaskParticipantsRequest(IReadOnlyList<Guid> UserIds);

internal sealed record SetTaskParticipantsCommand(Guid TaskId, IReadOnlyList<Guid> UserIds) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

/// <summary>The associated colleagues, as a whole set. The ones added are told; the holder is never among them.</summary>
internal sealed class SetTaskParticipantsHandler(
    LeadsDbContext db, TaskAccess access, ICurrentUser me, IAdministrationModule administration,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock)
    : IRequestHandler<SetTaskParticipantsCommand, Result>
{
    public const int Max = 20;

    public async Task<Result> Handle(SetTaskParticipantsCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: true, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);
        var task = loaded.Value!;
        if (task.IsClosed) return Result.Fail("TASK_CLOSED");

        var wanted = cmd.UserIds.Where(id => id != Guid.Empty && id != task.AssignedAgentId).Distinct().ToList();
        if (wanted.Count > Max) return Result.Fail("TASK_TOO_MANY_PARTICIPANTS");

        if (wanted.Count > 0)
        {
            var known = await TaskPeople.NamesAsync(administration, task.TenantId, wanted, ct);
            if (wanted.Any(id => !known.ContainsKey(id))) return Result.Fail("TASK_USER_NOT_FOUND");
        }

        var current = await db.CrmTaskParticipants.AsTracking().Where(p => p.TaskId == task.Id).ToListAsync(ct);
        db.CrmTaskParticipants.RemoveRange(current.Where(p => !wanted.Contains(p.UserId)));

        var now = clock.GetUtcNow();
        var added = wanted.Where(id => current.All(p => p.UserId != id)).ToList();
        foreach (var userId in added)
            db.CrmTaskParticipants.Add(CrmTaskParticipant.Add(task.TenantId, task.Id, userId, me.Id, now));

        var told = added.Where(id => id != me.Id).ToList();
        if (told.Count > 0)
            await publisher.PublishAsync(new CrmTaskActivityEvent(
                task.TenantId, task.Id, TaskActivityKind.Associated, me.Id, told, now.ToUnixTimeMilliseconds().ToString()), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Criteria ─────────────────────────────────────────────────────────────────────────────────

public sealed record AddTaskCriterionRequest(string Label);

public sealed record UpdateTaskCriterionRequest(string? Label, bool? IsMet);

internal sealed record AddTaskCriterionCommand(Guid TaskId, string Label) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed record UpdateTaskCriterionCommand(Guid TaskId, Guid CriterionId, string? Label, bool? IsMet) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed record RemoveTaskCriterionCommand(Guid TaskId, Guid CriterionId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

/// <summary>Criteria change only while the task is open: once it is closed, they say what it was closed on.</summary>
internal sealed class TaskCriteriaHandlers(LeadsDbContext db, TaskAccess access, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<AddTaskCriterionCommand, Result<Guid>>,
      IRequestHandler<UpdateTaskCriterionCommand, Result>,
      IRequestHandler<RemoveTaskCriterionCommand, Result>
{
    public async Task<Result<Guid>> Handle(AddTaskCriterionCommand cmd, CancellationToken ct)
    {
        var task = await OpenTaskAsync(cmd.TaskId, ct);
        if (task.IsFailure) return Result.Fail<Guid>(task.Error!);

        var existing = await db.CrmTaskCriteria.Where(c => c.TaskId == cmd.TaskId).Select(c => c.Position).ToListAsync(ct);
        if (existing.Count >= CrmTaskCriterion.MaxPerTask) return Result.Fail<Guid>("TASK_TOO_MANY_CRITERIA");

        var criterion = CrmTaskCriterion.Create(task.Value!.TenantId, cmd.TaskId, cmd.Label,
            existing.Count == 0 ? 0 : existing.Max() + 1, me.Id, clock.GetUtcNow());
        if (criterion.IsFailure) return Result.Fail<Guid>(criterion.Error!);

        db.CrmTaskCriteria.Add(criterion.Value!);
        await db.SaveChangesAsync(ct);
        return Result.Ok(criterion.Value!.Id);
    }

    public async Task<Result> Handle(UpdateTaskCriterionCommand cmd, CancellationToken ct)
    {
        var task = await OpenTaskAsync(cmd.TaskId, ct);
        if (task.IsFailure) return Result.Fail(task.Error!);

        var criterion = await db.CrmTaskCriteria.AsTracking().FirstOrDefaultAsync(c => c.Id == cmd.CriterionId && c.TaskId == cmd.TaskId, ct);
        if (criterion is null) return Result.Fail("TASK_CRITERION_NOT_FOUND");

        if (cmd.Label is not null)
        {
            var renamed = criterion.Rename(cmd.Label);
            if (renamed.IsFailure) return renamed;
        }

        if (cmd.IsMet is { } met) criterion.Mark(met, me.Id, clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> Handle(RemoveTaskCriterionCommand cmd, CancellationToken ct)
    {
        var task = await OpenTaskAsync(cmd.TaskId, ct);
        if (task.IsFailure) return Result.Fail(task.Error!);

        var criterion = await db.CrmTaskCriteria.AsTracking().FirstOrDefaultAsync(c => c.Id == cmd.CriterionId && c.TaskId == cmd.TaskId, ct);
        if (criterion is null) return Result.Fail("TASK_CRITERION_NOT_FOUND");

        db.CrmTaskCriteria.Remove(criterion);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private async Task<Result<CrmTask>> OpenTaskAsync(Guid taskId, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(taskId, tracking: false, ct);
        if (loaded.IsFailure) return loaded;
        return loaded.Value!.IsClosed ? Result.Fail<CrmTask>("TASK_CLOSED") : loaded;
    }
}

// ── Comments ─────────────────────────────────────────────────────────────────────────────────

public sealed record TaskCommentRequest(string Body);

internal sealed record ListTaskCommentsQuery(Guid TaskId, DateTimeOffset? Before, int Limit) : IRequest<Result<TaskCommentsPageDto>>;

internal sealed record AddTaskCommentCommand(Guid TaskId, string Body) : IRequest<Result<Guid>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed record EditTaskCommentCommand(Guid TaskId, Guid CommentId, string Body) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed record DeleteTaskCommentCommand(Guid TaskId, Guid CommentId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

/// <summary>
/// The thread. Anyone who may open the task may comment on it — closed or not: « pourquoi
/// a-t-elle été annulée ? » is asked afterwards. Only the author edits or deletes a comment.
/// </summary>
internal sealed class TaskCommentHandlers(
    LeadsDbContext db, TaskAccess access, ICurrentUser me, IAdministrationModule administration,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock)
    : IRequestHandler<ListTaskCommentsQuery, Result<TaskCommentsPageDto>>,
      IRequestHandler<AddTaskCommentCommand, Result<Guid>>,
      IRequestHandler<EditTaskCommentCommand, Result>,
      IRequestHandler<DeleteTaskCommentCommand, Result>
{
    public async Task<Result<TaskCommentsPageDto>> Handle(ListTaskCommentsQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<TaskCommentsPageDto>(loaded.Error!);

        var limit = Math.Clamp(query.Limit, 1, 100);
        var rows = await db.CrmTaskComments.AsNoTracking()
            .Where(c => c.TaskId == query.TaskId && (query.Before == null || c.CreatedAt < query.Before))
            .OrderByDescending(c => c.CreatedAt)
            .Take(limit + 1)
            .ToListAsync(ct);

        var more = rows.Count > limit;
        var page = rows.Take(limit).OrderBy(c => c.CreatedAt).ToList();
        var names = await TaskPeople.NamesAsync(administration, loaded.Value!.TenantId, page.Select(c => c.AuthorId).Distinct().ToList(), ct);

        return Result.Ok(new TaskCommentsPageDto(
            page.Select(c => new TaskCommentDto(
                c.Id, c.AuthorId, names.GetValueOrDefault(c.AuthorId) ?? "Utilisateur",
                c.DeletedAt.HasValue ? null : c.Body, c.CreatedAt, c.EditedAt, c.DeletedAt.HasValue, c.AuthorId == me.Id)).ToList(),
            more ? page[0].CreatedAt : null));
    }

    public async Task<Result<Guid>> Handle(AddTaskCommentCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<Guid>(loaded.Error!);
        var task = loaded.Value!;

        var comment = CrmTaskComment.Write(task.TenantId, task.Id, me.Id, cmd.Body, clock.GetUtcNow());
        if (comment.IsFailure) return Result.Fail<Guid>(comment.Error!);
        db.CrmTaskComments.Add(comment.Value!);

        var audience = await access.AudienceAsync(task, me.Id, ct);
        if (audience.Count > 0)
            await publisher.PublishAsync(new CrmTaskActivityEvent(
                task.TenantId, task.Id, TaskActivityKind.Commented, me.Id, audience, comment.Value!.Id.ToString("N")), ct);

        await db.SaveChangesAsync(ct);
        return Result.Ok(comment.Value!.Id);
    }

    public async Task<Result> Handle(EditTaskCommentCommand cmd, CancellationToken ct)
    {
        var comment = await LoadCommentAsync(cmd.TaskId, cmd.CommentId, ct);
        if (comment.IsFailure) return Result.Fail(comment.Error!);

        var edited = comment.Value!.Edit(me.Id, cmd.Body, clock.GetUtcNow());
        if (edited.IsFailure) return edited;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    public async Task<Result> Handle(DeleteTaskCommentCommand cmd, CancellationToken ct)
    {
        var comment = await LoadCommentAsync(cmd.TaskId, cmd.CommentId, ct);
        if (comment.IsFailure) return Result.Fail(comment.Error!);

        var deleted = comment.Value!.Delete(me.Id, clock.GetUtcNow());
        if (deleted.IsFailure) return deleted;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }

    private async Task<Result<CrmTaskComment>> LoadCommentAsync(Guid taskId, Guid commentId, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(taskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<CrmTaskComment>(loaded.Error!);

        var comment = await db.CrmTaskComments.AsTracking().FirstOrDefaultAsync(c => c.Id == commentId && c.TaskId == taskId, ct);
        return comment is null ? Result.Fail<CrmTaskComment>("TASK_COMMENT_NOT_FOUND") : Result.Ok(comment);
    }
}

// ── Tags ─────────────────────────────────────────────────────────────────────────────────────

public sealed record SetTaskTagsRequest(IReadOnlyList<string> Tags);

internal sealed record SetTaskTagsCommand(Guid TaskId, IReadOnlyList<string> Tags) : IRequest<Result<IReadOnlyList<string>>>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

/// <summary>The task's tags as a whole set; answers them normalised (lower case, trimmed).</summary>
internal sealed class SetTaskTagsHandler(LeadsDbContext db, TaskAccess access, ICurrentUser me, TimeProvider clock)
    : IRequestHandler<SetTaskTagsCommand, Result<IReadOnlyList<string>>>
{
    public async Task<Result<IReadOnlyList<string>>> Handle(SetTaskTagsCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<IReadOnlyList<string>>(loaded.Error!);
        var task = loaded.Value!;

        if (cmd.Tags.Any(t => !string.IsNullOrWhiteSpace(t) && CrmTaskTag.Normalize(t) is null))
            return Result.Fail<IReadOnlyList<string>>("TASK_TAG_TOO_LONG");

        var wanted = cmd.Tags.Select(CrmTaskTag.Normalize).OfType<string>().Distinct().ToList();
        if (wanted.Count > CrmTaskTag.MaxPerTask) return Result.Fail<IReadOnlyList<string>>("TASK_TOO_MANY_TAGS");

        var current = await db.CrmTaskTags.AsTracking().Where(t => t.TaskId == task.Id).ToListAsync(ct);
        db.CrmTaskTags.RemoveRange(current.Where(t => !wanted.Contains(t.Tag)));

        var now = clock.GetUtcNow();
        foreach (var tag in wanted.Where(w => current.All(c => c.Tag != w)))
            db.CrmTaskTags.Add(CrmTaskTag.Add(task.TenantId, task.Id, tag, me.Id, now));

        await db.SaveChangesAsync(ct);
        return Result.Ok<IReadOnlyList<string>>(wanted.OrderBy(t => t).ToList());
    }
}

// ── Attachments ──────────────────────────────────────────────────────────────────────────────

/// <summary>
/// An upload. NOT an <c>ICommand</c>, on purpose: <c>AuditBehavior</c> serialises a command's whole
/// payload, and this one is a file. The attachment row is its own record of who put what, when.
/// </summary>
internal sealed record StoreTaskAttachment(Guid TaskId, string FileName, byte[] Content) : IRequest<Result<TaskAttachmentDto>>;

internal sealed record RemoveTaskAttachmentCommand(Guid TaskId, Guid AttachmentId) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "CrmTask";
    public string? ResourceId => TaskId.ToString();
}

internal sealed record GetTaskAttachmentContentQuery(Guid TaskId, Guid AttachmentId) : IRequest<Result<TaskAttachmentContent>>;

internal sealed record TaskAttachmentContent(string FileName, string ContentType, byte[] Content);

internal sealed class TaskAttachmentHandlers(
    LeadsDbContext db, TaskAccess access, ICurrentUser me, IAdministrationModule administration,
    IServiceProvider services, IOptions<TaskAttachmentStorageOptions> options,
    [FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher, TimeProvider clock,
    ILogger<TaskAttachmentHandlers> logger)
    : IRequestHandler<StoreTaskAttachment, Result<TaskAttachmentDto>>,
      IRequestHandler<RemoveTaskAttachmentCommand, Result>,
      IRequestHandler<GetTaskAttachmentContentQuery, Result<TaskAttachmentContent>>
{
    /// <summary>No key configured = no store registered: uploads are refused, not silently dropped.</summary>
    private ITaskAttachmentStore? Store => services.GetService<ITaskAttachmentStore>();

    public async Task<Result<TaskAttachmentDto>> Handle(StoreTaskAttachment cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<TaskAttachmentDto>(loaded.Error!);
        var task = loaded.Value!;

        if (Store is not { } store) return Result.Fail<TaskAttachmentDto>("TASK_ATTACHMENTS_UNAVAILABLE");
        if (cmd.Content.Length == 0) return Result.Fail<TaskAttachmentDto>("TASK_ATTACHMENT_EMPTY");
        if (cmd.Content.Length > options.Value.MaxBytes) return Result.Fail<TaskAttachmentDto>("TASK_ATTACHMENT_TOO_LARGE");

        var contentType = TaskAttachmentTypes.Detect(cmd.Content, cmd.FileName);
        if (contentType is null) return Result.Fail<TaskAttachmentDto>("TASK_ATTACHMENT_TYPE_NOT_ALLOWED");

        var count = await db.CrmTaskAttachments.CountAsync(a => a.TaskId == task.Id && a.DeletedAt == null, ct);
        if (count >= CrmTaskAttachment.MaxPerTask) return Result.Fail<TaskAttachmentDto>("TASK_TOO_MANY_ATTACHMENTS");

        var stored = await store.StoreAsync(task.TenantId, cmd.Content, ct);
        var now = clock.GetUtcNow();
        var row = CrmTaskAttachment.Record(task.TenantId, task.Id, cmd.FileName, contentType, stored.SizeBytes, stored.Sha256, stored.StorageRef, me.Id, now);
        db.CrmTaskAttachments.Add(row);

        var audience = await access.AudienceAsync(task, me.Id, ct);
        if (audience.Count > 0)
            await publisher.PublishAsync(new CrmTaskActivityEvent(
                task.TenantId, task.Id, TaskActivityKind.FileAttached, me.Id, audience, row.Id.ToString("N")), ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Bytes nobody can reach are removed rather than left in the bucket.
            await store.DeleteAsync(task.TenantId, stored.StorageRef, CancellationToken.None);
            throw;
        }

        var names = await TaskPeople.NamesAsync(administration, task.TenantId, [me.Id], ct);
        return Result.Ok(new TaskAttachmentDto(row.Id, row.FileName, row.ContentType, row.SizeBytes, me.Id, names.GetValueOrDefault(me.Id) ?? "Utilisateur", now));
    }

    /// <summary>Its uploader, the task's holder or creator, or a bypass role may remove a file.</summary>
    public async Task<Result> Handle(RemoveTaskAttachmentCommand cmd, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(cmd.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail(loaded.Error!);
        var task = loaded.Value!;

        var row = await db.CrmTaskAttachments.AsTracking().FirstOrDefaultAsync(a => a.Id == cmd.AttachmentId && a.TaskId == task.Id && a.DeletedAt == null, ct);
        if (row is null) return Result.Fail("TASK_ATTACHMENT_NOT_FOUND");

        if (row.UploadedBy != me.Id && task.AssignedAgentId != me.Id && task.CreatedBy != me.Id && !access.HasBypassRole)
            return Result.Fail("TASK_ATTACHMENT_NOT_YOURS");

        row.Remove(me.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        if (Store is { } store)
        {
            try
            {
                await store.DeleteAsync(task.TenantId, row.StorageRef, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Task attachment {AttachmentId}: the bytes could not be deleted.", row.Id);
            }
        }

        return Result.Ok();
    }

    public async Task<Result<TaskAttachmentContent>> Handle(GetTaskAttachmentContentQuery query, CancellationToken ct)
    {
        var loaded = await access.LoadAsync(query.TaskId, tracking: false, ct);
        if (loaded.IsFailure) return Result.Fail<TaskAttachmentContent>(loaded.Error!);

        var row = await db.CrmTaskAttachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == query.AttachmentId && a.TaskId == query.TaskId && a.DeletedAt == null, ct);
        if (row is null || Store is not { } store) return Result.Fail<TaskAttachmentContent>("TASK_ATTACHMENT_NOT_FOUND");

        var bytes = await store.OpenAsync(row.TenantId, row.StorageRef, ct);
        return bytes is null
            ? Result.Fail<TaskAttachmentContent>("TASK_ATTACHMENT_NOT_FOUND")
            : Result.Ok(new TaskAttachmentContent(row.FileName, row.ContentType, bytes));
    }
}

/// <summary>
/// What may be attached, read from the bytes and not from the browser's word: PDF, JPEG, PNG,
/// Office documents (modern and legacy) and plain text or CSV. Office formats are containers
/// (ZIP, OLE) that only the extension tells apart, so for those the extension must agree with the
/// container. Anything else — an executable, an HTML page — is refused.
/// </summary>
internal static class TaskAttachmentTypes
{
    private static readonly Dictionary<string, string> Ooxml = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    private static readonly Dictionary<string, string> Ole = new(StringComparer.OrdinalIgnoreCase)
    {
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
    };

    public static string? Detect(byte[] content, string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty);
        ReadOnlySpan<byte> head = content.AsSpan(0, Math.Min(content.Length, 8));

        if (head.StartsWith("%PDF"u8)) return "application/pdf";
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return "image/jpeg";
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 })) return "image/png";
        if (head.StartsWith("PK\u0003\u0004"u8)) return Ooxml.GetValueOrDefault(ext);
        if (head.StartsWith(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 })) return Ole.GetValueOrDefault(ext);

        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase) || ext.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            var sample = content.AsSpan(0, Math.Min(content.Length, 8192));
            if (sample.Contains((byte)0)) return null;
            return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv" : "text/plain";
        }

        return null;
    }
}
