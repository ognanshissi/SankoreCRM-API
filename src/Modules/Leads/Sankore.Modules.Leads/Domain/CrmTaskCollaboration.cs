namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>What a task may be about. No value = an internal task, about nothing in particular.</summary>
public enum TaskEntityType
{
    Lead,
    Client,
    KycFile,
    /// <summary>A project (<c>Opportunity</c>) — owner, 2026-10-27: its page lists its tasks.</summary>
    Project,
}

/// <summary>
/// A colleague associated with a task besides its holder — who follows it, comments, attaches
/// files and ticks criteria. Being associated grants no assignment: the task stays the holder's.
/// </summary>
public sealed class CrmTaskParticipant : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AddedBy { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    private CrmTaskParticipant() { }

    public static CrmTaskParticipant Add(Guid tenantId, Guid taskId, Guid userId, Guid addedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TaskId = taskId,
        UserId = userId,
        AddedBy = addedBy,
        AddedAt = now,
    };
}

/// <summary>
/// One criterion of a task — what « done » means, ticked one by one. Who ticked it and when is kept:
/// a criterion is a commitment, and « c'était fait » is a claim somebody made.
/// </summary>
public sealed class CrmTaskCriterion : ITenant
{
    public const int LabelMaxLength = 300;
    public const int MaxPerTask = 30;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public string Label { get; private set; } = default!;
    public int Position { get; private set; }
    public bool IsMet { get; private set; }
    public Guid? MetBy { get; private set; }
    public DateTimeOffset? MetAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private CrmTaskCriterion() { }

    public static Result<CrmTaskCriterion> Create(Guid tenantId, Guid taskId, string label, int position, Guid createdBy, DateTimeOffset now)
    {
        var clean = label?.Trim() ?? string.Empty;
        if (clean.Length == 0) return Result.Fail<CrmTaskCriterion>("TASK_CRITERION_LABEL_REQUIRED");
        if (clean.Length > LabelMaxLength) return Result.Fail<CrmTaskCriterion>("TASK_CRITERION_LABEL_TOO_LONG");

        return Result.Ok(new CrmTaskCriterion
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TaskId = taskId,
            Label = clean,
            Position = position,
            CreatedBy = createdBy,
            CreatedAt = now,
        });
    }

    public Result Rename(string label)
    {
        var clean = label?.Trim() ?? string.Empty;
        if (clean.Length == 0) return Result.Fail("TASK_CRITERION_LABEL_REQUIRED");
        if (clean.Length > LabelMaxLength) return Result.Fail("TASK_CRITERION_LABEL_TOO_LONG");
        Label = clean;
        return Result.Ok();
    }

    public void Mark(bool met, Guid actorId, DateTimeOffset now)
    {
        if (met == IsMet) return;
        IsMet = met;
        MetBy = met ? actorId : null;
        MetAt = met ? now : null;
    }
}

/// <summary>
/// A comment on a task. Edited in place by its author (the time of the edit is shown); deleted
/// LOGICALLY — the thread keeps « commentaire supprimé » so the replies around it still read.
/// </summary>
public sealed class CrmTaskComment : ITenant
{
    public const int BodyMaxLength = 4000;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Body { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? EditedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }

    private CrmTaskComment() { }

    public static Result<CrmTaskComment> Write(Guid tenantId, Guid taskId, Guid authorId, string body, DateTimeOffset now)
    {
        var clean = body?.Trim() ?? string.Empty;
        if (clean.Length == 0) return Result.Fail<CrmTaskComment>("TASK_COMMENT_EMPTY");
        if (clean.Length > BodyMaxLength) return Result.Fail<CrmTaskComment>("TASK_COMMENT_TOO_LONG");

        return Result.Ok(new CrmTaskComment
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            TaskId = taskId,
            AuthorId = authorId,
            Body = clean,
            CreatedAt = now,
        });
    }

    public Result Edit(Guid actorId, string body, DateTimeOffset now)
    {
        if (actorId != AuthorId) return Result.Fail("TASK_COMMENT_NOT_YOURS");
        if (DeletedAt.HasValue) return Result.Fail("TASK_COMMENT_DELETED");
        var clean = body?.Trim() ?? string.Empty;
        if (clean.Length == 0) return Result.Fail("TASK_COMMENT_EMPTY");
        if (clean.Length > BodyMaxLength) return Result.Fail("TASK_COMMENT_TOO_LONG");
        if (clean == Body) return Result.Ok();

        Body = clean;
        EditedAt = now;
        return Result.Ok();
    }

    /// <summary>The text goes; the row stays as « supprimé ».</summary>
    public Result Delete(Guid actorId, DateTimeOffset now)
    {
        if (actorId != AuthorId) return Result.Fail("TASK_COMMENT_NOT_YOURS");
        if (DeletedAt.HasValue) return Result.Ok();
        DeletedAt = now;
        Body = string.Empty;
        return Result.Ok();
    }
}

/// <summary>A tag on a task — lower-cased and trimmed like a lead's, one row per tag.</summary>
public sealed class CrmTaskTag : ITenant
{
    public const int MaxLength = 50;
    public const int MaxPerTask = 20;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public string Tag { get; private set; } = default!;
    public Guid AddedBy { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }

    private CrmTaskTag() { }

    public static string? Normalize(string? tag)
    {
        var clean = tag?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(clean) || clean.Length > MaxLength ? null : clean;
    }

    public static CrmTaskTag Add(Guid tenantId, Guid taskId, string normalizedTag, Guid addedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TaskId = taskId,
        Tag = normalizedTag,
        AddedBy = addedBy,
        AddedAt = now,
    };
}

/// <summary>
/// A file attached to a task. The bytes are in the encrypted task-attachment store; this row says
/// what they are and who put them there. Removal is LOGICAL for the row (the history of the task
/// says a file was there) and physical for the bytes.
/// </summary>
public sealed class CrmTaskAttachment : ITenant
{
    public const int FileNameMaxLength = 255;
    public const int MaxPerTask = 30;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TaskId { get; private set; }
    public string FileName { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = default!;
    public string StorageRef { get; private set; } = default!;
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    private CrmTaskAttachment() { }

    public static CrmTaskAttachment Record(
        Guid tenantId, Guid taskId, string fileName, string contentType, long sizeBytes, string sha256,
        string storageRef, Guid uploadedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        TaskId = taskId,
        FileName = SafeName(fileName),
        ContentType = contentType,
        SizeBytes = sizeBytes,
        Sha256 = sha256,
        StorageRef = storageRef,
        UploadedBy = uploadedBy,
        UploadedAt = now,
    };

    public void Remove(Guid actorId, DateTimeOffset now)
    {
        if (DeletedAt.HasValue) return;
        DeletedAt = now;
        DeletedBy = actorId;
    }

    /// <summary>The name as shown and offered on download: no path, no control character, bounded.</summary>
    public static string SafeName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        name = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '\\' or '/')).ToArray()).Trim();
        if (name.Length == 0) name = "fichier";
        return name.Length <= FileNameMaxLength ? name : name[^FileNameMaxLength..];
    }
}
