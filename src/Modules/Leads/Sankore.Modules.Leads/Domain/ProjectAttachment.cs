namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A file given for an « Attachment » field of a project's template (product owner, 2026-10-26):
/// the signed estimate, a photo of the site. The bytes are in the encrypted task-attachment store
/// — same kind of content, same key — and this row says whose project, which field, who put it.
/// Removed logically: the row stays, so the trail of what was once attached does too.
/// </summary>
/// <summary>How a project's document left (project documents lot 4).</summary>
public static class ProjectDocumentChannels
{
    public const string Email = "Email";
    public const string WhatsApp = "WhatsApp";
}

public sealed class ProjectAttachment : ITenant
{
    public const int MaxPerField = 10;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OpportunityId { get; private set; }

    /// <summary>The template field's key — see <see cref="ProjectFieldDefinition.Key"/>.</summary>
    public string FieldKey { get; private set; } = default!;

    public string FileName { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long SizeBytes { get; private set; }
    public string Sha256 { get; private set; } = default!;
    public string StorageRef { get; private set; } = default!;
    public Guid UploadedBy { get; private set; }
    public DateTimeOffset UploadedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public Guid? DeletedBy { get; private set; }

    /// <summary>
    /// When the file last LEFT for the client or the lead (project documents lot 4, 2026-11-08): the
    /// provider accepted the e-mail or the WhatsApp message carrying it. A devis sent is a document
    /// handed over — the client's « Documents » tab says « Remis » from then on.
    /// </summary>
    public DateTimeOffset? LastSentAt { get; private set; }

    /// <summary><see cref="ProjectDocumentChannels.Email"/> or <see cref="ProjectDocumentChannels.WhatsApp"/>.</summary>
    public string? LastSentChannel { get; private set; }

    public void MarkSent(string channel, DateTimeOffset at)
    {
        if (LastSentAt is { } previous && previous > at) return;
        LastSentAt = at;
        LastSentChannel = channel;
    }

    private ProjectAttachment() { }

    public static ProjectAttachment Record(
        Guid tenantId, Guid opportunityId, string fieldKey, string fileName, string contentType, long sizeBytes,
        string sha256, string storageRef, Guid uploadedBy, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        OpportunityId = opportunityId,
        FieldKey = fieldKey,
        FileName = CrmTaskAttachment.SafeName(fileName),
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
}
