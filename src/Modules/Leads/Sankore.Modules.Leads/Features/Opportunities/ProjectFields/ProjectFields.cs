namespace Sankore.Modules.Leads.Features.Opportunities.ProjectFields;

using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Tasks.Collaboration;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.Infrastructure.TaskAttachments;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>A file of a project, as the page lists it.</summary>
public sealed record ProjectAttachmentDto(
    Guid Id, string FieldKey, string FileName, string ContentType, long SizeBytes, DateTimeOffset UploadedAt);

/// <summary>
/// The template's fields read as a tenant form, so a project's values go through the same checks
/// as a lead's or a client's custom values (<see cref="CustomFieldValues"/>) — one definition of
/// « is this value acceptable » across the product. Attachment fields are left out: their values
/// are files, not JSON.
/// </summary>
internal static class ProjectFieldForms
{
    public static EntityFormDto FormOf(IReadOnlyList<ProjectFieldDefinition> fields)
        => new(
            // The entity names the record kind a form belongs to; nothing in the check reads it.
            FormEntity.Lead,
            Version: 0,
            Sections: [],
            Fields: fields
                .Where(f => f.Type != ProjectFieldType.Attachment)
                .Select((f, i) => new FormField(
                    f.Key,
                    Enum.Parse<FormFieldType>(f.Type.ToString()),
                    f.Label,
                    i,
                    HelpText: f.HelpText,
                    Options: f.Options?.Select(o => new FormOption(o.Value, o.Label)).ToList()))
                .ToList());

    /// <summary>The keys holding a value — a JSON null or a blank string holds none.</summary>
    public static HashSet<string> FilledKeys(string? json)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return keys;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return keys;

        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var blank = p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (p.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(p.Value.GetString()))
                || (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0);
            if (!blank) keys.Add(p.Name);
        }
        return keys;
    }

    /// <summary>
    /// The required fields still empty on <paramref name="project"/>: a value for an ordinary field,
    /// at least one file for an attachment. A key the template no longer declares is ignored — a
    /// field removed from the template cannot be filled any more.
    /// </summary>
    public static async Task<IReadOnlyList<ProjectFieldDefinition>> MissingAsync(
        LeadsDbContext db, Opportunity project, IReadOnlyList<ProjectFieldDefinition> fields,
        IEnumerable<string> requiredKeys, CancellationToken ct)
    {
        var required = requiredKeys.ToHashSet(StringComparer.Ordinal);
        var wanted = fields.Where(f => required.Contains(f.Key)).ToList();
        if (wanted.Count == 0) return [];

        var filled = FilledKeys(project.CustomFieldsJson);
        var withFiles = wanted.Any(f => f.Type == ProjectFieldType.Attachment)
            ? (await db.ProjectAttachments.AsNoTracking()
                .Where(a => a.OpportunityId == project.Id && a.DeletedAt == null)
                .Select(a => a.FieldKey).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal)
            : [];

        return wanted
            .Where(f => f.Type == ProjectFieldType.Attachment ? !withFiles.Contains(f.Key) : !filled.Contains(f.Key))
            .ToList();
    }
}

// ── Values ───────────────────────────────────────────────────────────────────────────────

/// <summary>Merges values into the project: a key with a value overwrites, null clears, absent keeps.</summary>
internal sealed record SetProjectFieldsCommand(Guid OpportunityId, IReadOnlyDictionary<string, JsonElement> Values)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class SetProjectFieldsHandler(LeadsDbContext db, ProjectTemplateResolver templates, TimeProvider clock)
    : IRequestHandler<SetProjectFieldsCommand, Result>
{
    public async Task<Result> Handle(SetProjectFieldsCommand cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail(OpportunityErrors.NotFound);

        var template = await templates.ResolveAsync(project.Product, ct);
        var form = ProjectFieldForms.FormOf(template.Fields ?? []);

        var merged = CustomFieldValues.Apply(form, project.CustomFieldsJson, cmd.Values, null, enforceRequired: false, isCreate: false);
        if (merged.IsFailure) return Result.Fail(merged.Error!);

        var set = project.SetCustomFields(merged.Value, clock.GetUtcNow());
        if (set.IsFailure) return set;

        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

// ── Attachments ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// An upload. NOT an <c>ICommand</c>, like a task's: <c>AuditBehavior</c> serialises a command's
/// whole payload, and this one is a file. The attachment row is its own record of who put what.
/// </summary>
internal sealed record StoreProjectAttachment(Guid OpportunityId, string FieldKey, string FileName, byte[] Content)
    : IRequest<Result<ProjectAttachmentDto>>;

internal sealed record RemoveProjectAttachmentCommand(Guid OpportunityId, Guid AttachmentId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed record GetProjectAttachmentContentQuery(Guid OpportunityId, Guid AttachmentId)
    : IRequest<Result<TaskAttachmentContent>>;

internal sealed class ProjectAttachmentHandlers(
    LeadsDbContext db, ProjectTemplateResolver templates, ICurrentUser me, IServiceProvider services,
    IOptions<TaskAttachmentStorageOptions> options, TimeProvider clock, ILogger<ProjectAttachmentHandlers> logger)
    : IRequestHandler<StoreProjectAttachment, Result<ProjectAttachmentDto>>,
      IRequestHandler<RemoveProjectAttachmentCommand, Result>,
      IRequestHandler<GetProjectAttachmentContentQuery, Result<TaskAttachmentContent>>
{
    public const string Unavailable = "PROJECT_ATTACHMENTS_UNAVAILABLE";
    public const string FieldNotAttachment = "PROJECT_FIELD_NOT_ATTACHMENT";
    public const string NotFound = "PROJECT_ATTACHMENT_NOT_FOUND";
    public const string TooMany = "PROJECT_TOO_MANY_ATTACHMENTS";

    /// <summary>The task-attachment store: no key configured = none registered, uploads refused.</summary>
    private ITaskAttachmentStore? Store => services.GetService<ITaskAttachmentStore>();

    public async Task<Result<ProjectAttachmentDto>> Handle(StoreProjectAttachment cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail<ProjectAttachmentDto>(OpportunityErrors.NotFound);
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail<ProjectAttachmentDto>(OpportunityErrors.AlreadyClosed);

        var template = await templates.ResolveAsync(project.Product, ct);
        var field = (template.Fields ?? []).FirstOrDefault(f => f.Key == cmd.FieldKey);
        if (field is null || field.Type != ProjectFieldType.Attachment)
            return Result.Fail<ProjectAttachmentDto>(FieldNotAttachment);

        if (Store is not { } store) return Result.Fail<ProjectAttachmentDto>(Unavailable);
        if (cmd.Content.Length == 0) return Result.Fail<ProjectAttachmentDto>("TASK_ATTACHMENT_EMPTY");
        if (cmd.Content.Length > options.Value.MaxBytes) return Result.Fail<ProjectAttachmentDto>("TASK_ATTACHMENT_TOO_LARGE");

        var contentType = TaskAttachmentTypes.Detect(cmd.Content, cmd.FileName);
        if (contentType is null) return Result.Fail<ProjectAttachmentDto>("TASK_ATTACHMENT_TYPE_NOT_ALLOWED");

        var count = await db.ProjectAttachments.CountAsync(
            a => a.OpportunityId == project.Id && a.FieldKey == field.Key && a.DeletedAt == null, ct);
        if (count >= ProjectAttachment.MaxPerField) return Result.Fail<ProjectAttachmentDto>(TooMany);

        var stored = await store.StoreAsync(project.TenantId, cmd.Content, ct);
        var row = ProjectAttachment.Record(
            project.TenantId, project.Id, field.Key, cmd.FileName, contentType, stored.SizeBytes, stored.Sha256,
            stored.StorageRef, me.Id, clock.GetUtcNow());
        db.ProjectAttachments.Add(row);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Bytes nobody can reach are removed rather than left in the bucket.
            await store.DeleteAsync(project.TenantId, stored.StorageRef, CancellationToken.None);
            throw;
        }

        return Result.Ok(ToDto(row));
    }

    public async Task<Result> Handle(RemoveProjectAttachmentCommand cmd, CancellationToken ct)
    {
        var project = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail(OpportunityErrors.NotFound);
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail(OpportunityErrors.AlreadyClosed);

        var row = await db.ProjectAttachments.AsTracking().FirstOrDefaultAsync(
            a => a.Id == cmd.AttachmentId && a.OpportunityId == project.Id && a.DeletedAt == null, ct);
        if (row is null) return Result.Fail(NotFound);

        row.Remove(me.Id, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        if (Store is { } store)
        {
            try
            {
                await store.DeleteAsync(project.TenantId, row.StorageRef, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Project attachment {AttachmentId}: the bytes could not be deleted.", row.Id);
            }
        }

        return Result.Ok();
    }

    public async Task<Result<TaskAttachmentContent>> Handle(GetProjectAttachmentContentQuery query, CancellationToken ct)
    {
        var row = await db.ProjectAttachments.AsNoTracking().FirstOrDefaultAsync(
            a => a.Id == query.AttachmentId && a.OpportunityId == query.OpportunityId && a.DeletedAt == null, ct);
        if (row is null || Store is not { } store) return Result.Fail<TaskAttachmentContent>(NotFound);

        var bytes = await store.OpenAsync(row.TenantId, row.StorageRef, ct);
        return bytes is null
            ? Result.Fail<TaskAttachmentContent>(NotFound)
            : Result.Ok(new TaskAttachmentContent(row.FileName, row.ContentType, bytes));
    }

    public static ProjectAttachmentDto ToDto(ProjectAttachment a)
        => new(a.Id, a.FieldKey, a.FileName, a.ContentType, a.SizeBytes, a.UploadedAt);
}
