namespace Sankore.Modules.Leads.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// One message of a <see cref="WhatsAppConversation"/>, in either direction (WA-BE-06 → WA-BE-08).
///
/// <para>
/// <b>The text is encrypted in its column</b>, like every free text this module holds about a
/// person — a conversation is exactly where a customer writes an amount, an illness or an
/// identity number. So are a template's parameters, which are kept because the consumer that
/// sends the message reads them minutes later in another process.
/// </para>
///
/// <para>
/// <b><see cref="ProviderMessageId"/> is unique per tenant</b> — Meta's <c>wamid</c>. It is what a
/// status names, and what makes recording an inbound message idempotent: a webhook replayed by its
/// job, or redelivered by the provider in another batch, finds the row it already wrote.
/// </para>
/// </summary>
public sealed class WhatsAppMessage : ITenant
{
    public const int BodyMaxLength = 4096;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }

    public WhatsAppDirection Direction { get; private set; }
    public WhatsAppAuthor Author { get; private set; }

    /// <summary>The agent who sent it. Taken from the authenticated caller, never from a request.</summary>
    public Guid? AuthorUserId { get; private set; }

    public WhatsAppMessageKind Kind { get; private set; }

    /// <summary>
    /// What the bubble shows: the text, the caption, the label of a pressed button, or — for a
    /// template — the body as it was rendered with its values. Plaintext here, ciphertext in the column.
    /// </summary>
    public string? Body { get; private set; }

    public Guid? TemplateId { get; private set; }
    public string? TemplateName { get; private set; }
    public string? TemplateLanguage { get; private set; }

    /// <summary>
    /// The template's values as JSON, encrypted — what the send consumer needs, and nothing reads
    /// otherwise. Blanked once the message has left.
    /// </summary>
    public string? TemplateParameters { get; private set; }

    /// <summary>The provider's id of an inbound media — downloaded on demand, never stored.</summary>
    public string? MediaId { get; private set; }
    public string? MediaMimeType { get; private set; }

    /// <summary>The file name the customer gave. Encrypted: it is often their own name.</summary>
    public string? MediaFileName { get; private set; }

    /// <summary>Meta's <c>wamid</c>. Null until the provider accepted an outbound message.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>The message this one answers, when the customer used « Répondre ».</summary>
    public string? ReplyToProviderMessageId { get; private set; }

    public WhatsAppMessageStatus Status { get; private set; }

    /// <summary>When the provider says it happened — the customer's send, or our send's acceptance.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }

    /// <summary>One of the integration module's <c>MESSAGING_*</c> codes, or this module's own.</summary>
    public string? ErrorCode { get; private set; }

    /// <summary>The provider's sentence about the failure. Never personal data.</summary>
    public string? ErrorDetail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    // ── Stored media (WA-BE-20, WA-BE-21) ─────────────────────────────────

    /// <summary>The reference of the encrypted copy in the media store, null when none was kept.</summary>
    public string? MediaStorageRef { get; private set; }

    public long? MediaSizeBytes { get; private set; }

    /// <summary>SHA-256 of the plaintext, lower-case hex.</summary>
    public string? MediaSha256 { get; private set; }

    public DateTimeOffset? MediaStoredAt { get; private set; }

    /// <summary>When the media was deleted for retention. The bubble then says so.</summary>
    public DateTimeOffset? MediaPurgedAt { get; private set; }

    /// <summary>
    /// Never part of the history sent to the bot's model: an answer given to the complaint intake
    /// — a client number, a date of birth, the complaint itself (2026-10-26). The agents still read it.
    /// </summary>
    public bool ExcludedFromModel { get; private set; }

    public void ExcludeFromModel() => ExcludedFromModel = true;

    private WhatsAppMessage() { } // EF Core

    public static WhatsAppMessage Inbound(
        Guid tenantId,
        Guid conversationId,
        string providerMessageId,
        WhatsAppMessageKind kind,
        string? body,
        string? mediaId,
        string? mediaMimeType,
        string? mediaFileName,
        string? replyToProviderMessageId,
        DateTimeOffset sentAt,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(providerMessageId))
            throw new DomainException("An inbound message carries the provider's id.");

        return new WhatsAppMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Direction = WhatsAppDirection.Inbound,
            Author = WhatsAppAuthor.Contact,
            Kind = kind,
            Body = Clip(body, BodyMaxLength),
            MediaId = Clip(mediaId, 200),
            MediaMimeType = Clip(mediaMimeType, 100),
            MediaFileName = Clip(mediaFileName, 255),
            ProviderMessageId = providerMessageId.Trim(),
            ReplyToProviderMessageId = Clip(replyToProviderMessageId, 200),
            Status = WhatsAppMessageStatus.Received,
            OccurredAt = sentAt,
            CreatedAt = now,
        };
    }

    public static WhatsAppMessage OutboundTemplate(
        Guid tenantId,
        Guid conversationId,
        Guid authorUserId,
        Guid templateId,
        string templateName,
        string templateLanguage,
        string renderedBody,
        string parametersJson,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Direction = WhatsAppDirection.Outbound,
            Author = WhatsAppAuthor.Agent,
            AuthorUserId = authorUserId,
            Kind = WhatsAppMessageKind.Template,
            Body = Clip(renderedBody, BodyMaxLength),
            TemplateId = templateId,
            TemplateName = templateName,
            TemplateLanguage = templateLanguage,
            TemplateParameters = parametersJson,
            Status = WhatsAppMessageStatus.Queued,
            OccurredAt = now,
            CreatedAt = now,
        };

    /// <summary>
    /// Free text (WA-BE-16), queued for the send consumer like a template. Written by an agent,
    /// or by the platform (<see cref="WhatsAppAuthor.System"/>) with no author id.
    /// </summary>
    public static WhatsAppMessage OutboundText(
        Guid tenantId,
        Guid conversationId,
        WhatsAppAuthor author,
        Guid? authorUserId,
        string text,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DomainException("A message carries text.");
        if (author == WhatsAppAuthor.Contact) throw new DomainException("An outbound message is not the customer's.");

        return new WhatsAppMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Direction = WhatsAppDirection.Outbound,
            Author = author,
            AuthorUserId = authorUserId,
            Kind = WhatsAppMessageKind.Text,
            Body = Clip(text.Trim(), BodyMaxLength),
            Status = WhatsAppMessageStatus.Queued,
            OccurredAt = now,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// An image or a document an agent sent (WA-BE-16). Sent synchronously by the handler — the
    /// bytes are in the request and nowhere else — so it is created already handed over: either
    /// <see cref="MarkSent"/> or <see cref="MarkFailed"/> follows at once.
    /// </summary>
    public static WhatsAppMessage OutboundMedia(
        Guid tenantId,
        Guid conversationId,
        Guid authorUserId,
        WhatsAppMessageKind kind,
        string mimeType,
        string fileName,
        string? caption,
        DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConversationId = conversationId,
            Direction = WhatsAppDirection.Outbound,
            Author = WhatsAppAuthor.Agent,
            AuthorUserId = authorUserId,
            Kind = kind,
            Body = Clip(caption?.Trim(), 1024),
            MediaMimeType = Clip(mimeType, 100),
            MediaFileName = Clip(fileName, 255),
            Status = WhatsAppMessageStatus.Sending,
            OccurredAt = now,
            CreatedAt = now,
        };

    /// <summary>The media id the provider gave the upload, so the thread can show it again.</summary>
    public void AttachProviderMedia(string mediaId) => MediaId = Clip(mediaId, 200);

    /// <summary>The bytes were stored, envelope-encrypted, in the platform's own store (WA-BE-20).</summary>
    public void AttachStoredMedia(string storageRef, long sizeBytes, string sha256, DateTimeOffset now)
    {
        MediaStorageRef = storageRef;
        MediaSizeBytes = sizeBytes;
        MediaSha256 = sha256;
        MediaStoredAt = now;
    }

    /// <summary>
    /// The media reached its retention (WA-BE-21): our copy is deleted, the provider was asked to
    /// delete theirs, and the bubble says the file is gone rather than offering a dead link.
    /// </summary>
    public void MarkMediaPurged(DateTimeOffset now)
    {
        MediaStorageRef = null;
        MediaId = null;
        MediaPurgedAt = now;
    }

    /// <summary>A media the thread can still open — from our store or from the provider.</summary>
    public bool HasMedia => MediaStorageRef is not null || MediaId is not null;

    /// <summary>
    /// The consumer takes the message. Answers false when it was already taken — by a redelivered
    /// event, or by a consumer that crashed mid-call — and then it is NOT sent again.
    /// </summary>
    public bool TryBeginSending()
    {
        if (Status != WhatsAppMessageStatus.Queued) return false;

        Status = WhatsAppMessageStatus.Sending;
        return true;
    }

    /// <summary>The provider accepted it. Its parameters are no longer needed and are dropped.</summary>
    public void MarkSent(string providerMessageId, DateTimeOffset at)
    {
        ProviderMessageId = providerMessageId;
        SentAt ??= at;
        OccurredAt = at;
        TemplateParameters = null;

        // A status may have overtaken this save (see the webhook job); never step back from it.
        if (Status is WhatsAppMessageStatus.Queued or WhatsAppMessageStatus.Sending)
            Status = WhatsAppMessageStatus.Sent;
    }

    /// <summary>Refused before or by the provider. The parameters go too: nothing will read them.</summary>
    public void MarkFailed(string code, string? detail, DateTimeOffset at)
    {
        Status = WhatsAppMessageStatus.Failed;
        FailedAt = at;
        ErrorCode = Clip(code, 60);
        ErrorDetail = Clip(detail, 300);
        TemplateParameters = null;
    }

    /// <summary>
    /// A delivery status from the provider (WA-BE-08). Forward only: a status earlier than the one
    /// held is ignored, and answers false. A failure is applied over « envoyé » — Meta does report a
    /// failure after accepting — but never over « livré » or « lu », which it would contradict.
    /// </summary>
    public bool ApplyStatus(string status, DateTimeOffset at, string? errorCode, string? errorDetail)
    {
        if (Direction != WhatsAppDirection.Outbound) return false;

        switch (status)
        {
            case "sent":
                SentAt ??= at;
                return Advance(WhatsAppMessageStatus.Sent);

            case "delivered":
                SentAt ??= at;
                DeliveredAt ??= at;
                return Advance(WhatsAppMessageStatus.Delivered);

            case "read":
                SentAt ??= at;
                DeliveredAt ??= at;
                ReadAt ??= at;
                return Advance(WhatsAppMessageStatus.Read);

            case "failed":
                if (Status is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read or WhatsAppMessageStatus.Failed)
                    return false;

                MarkFailed(errorCode ?? "MESSAGING_NOT_DELIVERED", errorDetail, at);
                return true;

            default:
                return false;
        }
    }

    private bool Advance(WhatsAppMessageStatus target)
    {
        // Failed is not "further" than Read in meaning, but a failed message that later reports
        // delivered was delivered after all: Meta retried. The later truth wins.
        if (Status == WhatsAppMessageStatus.Failed || (int)target > (int)Status)
        {
            Status = target;

            if (target is WhatsAppMessageStatus.Delivered or WhatsAppMessageStatus.Read)
            {
                ErrorCode = null;
                ErrorDetail = null;
                FailedAt = null;
            }

            return true;
        }

        return false;
    }

    /// <summary>Blanks the content, keeping the facts that a message existed and when (anonymisation).</summary>
    internal void Anonymize()
    {
        Body = null;
        TemplateParameters = null;
        MediaFileName = null;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
