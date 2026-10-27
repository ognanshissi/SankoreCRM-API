namespace Sankore.Modules.Integration.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// How a mail server is reached. There is no « none »: the password would cross the network in
/// clear, and a server that offers nothing better is not one the CRM logs into.
/// </summary>
public enum MailServerSecurity
{
    /// <summary>TLS from the first byte — IMAP 993, SMTP 465.</summary>
    Ssl,

    /// <summary>Plain connection upgraded by STARTTLS, which is REQUIRED — IMAP 143, SMTP 587.</summary>
    StartTls,
}

/// <summary>
/// The servers of an <see cref="ConnectedAccountProvider.Imap"/> account (connected mailbox,
/// lot 1): where to read (IMAP) and where to send (SMTP), and the login. One row per account,
/// keyed by it. The password is NOT here — it is in the vault under the account's id
/// (<c>ConnectedAccountSecrets.MailPassword</c>), like the OAuth tokens of the other providers.
///
/// <para>
/// <b>The ports are a closed list</b> (<see cref="ImapPorts"/>, <see cref="SmtpPorts"/>): the host
/// is typed by a user and the CRM opens a connection to it from inside its own network, so the
/// pair must not be a way to probe arbitrary services. The addresses are checked at connection
/// time by <c>MailServerEgress</c>, the same rule as the SFTP deposits.
/// </para>
/// </summary>
public sealed class MailServerSettings : AggregateRoot
{
    public const int HostMaxLength = 253;
    public const int UsernameMaxLength = 320;

    public static readonly IReadOnlySet<int> ImapPorts = new HashSet<int> { 993, 143 };
    public static readonly IReadOnlySet<int> SmtpPorts = new HashSet<int> { 465, 587, 25, 2525 };

    private MailServerSettings() { }

    public Guid AccountId { get; private set; }
    public Guid UserId { get; private set; }
    public string ImapHost { get; private set; } = default!;
    public int ImapPort { get; private set; }
    public MailServerSecurity ImapSecurity { get; private set; }
    public string SmtpHost { get; private set; } = default!;
    public int SmtpPort { get; private set; }
    public MailServerSecurity SmtpSecurity { get; private set; }

    /// <summary>The login on both servers — usually the address, not always.</summary>
    public string Username { get; private set; } = default!;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<MailServerSettings> Create(Guid tenantId, Guid accountId, Guid userId, MailServerEndpoints endpoints, DateTimeOffset now)
    {
        var checkedEndpoints = endpoints.Validate();
        if (checkedEndpoints.IsFailure) return Result.Fail<MailServerSettings>(checkedEndpoints.Error!);

        var settings = new MailServerSettings { TenantId = tenantId, AccountId = accountId, UserId = userId };
        settings.Apply(checkedEndpoints.Value!, now);
        return Result.Ok(settings);
    }

    public Result Update(MailServerEndpoints endpoints, DateTimeOffset now)
    {
        var checkedEndpoints = endpoints.Validate();
        if (checkedEndpoints.IsFailure) return Result.Fail(checkedEndpoints.Error!);

        Apply(checkedEndpoints.Value!, now);
        return Result.Ok();
    }

    public MailServerEndpoints Endpoints =>
        new(ImapHost, ImapPort, ImapSecurity, SmtpHost, SmtpPort, SmtpSecurity, Username);

    private void Apply(MailServerEndpoints e, DateTimeOffset now)
    {
        ImapHost = e.ImapHost;
        ImapPort = e.ImapPort;
        ImapSecurity = e.ImapSecurity;
        SmtpHost = e.SmtpHost;
        SmtpPort = e.SmtpPort;
        SmtpSecurity = e.SmtpSecurity;
        Username = e.Username;
        UpdatedAt = now;
    }
}

/// <summary>An IMAP + SMTP pair as typed, before or after it is saved. Never carries the password.</summary>
public sealed record MailServerEndpoints(
    string ImapHost,
    int ImapPort,
    MailServerSecurity ImapSecurity,
    string SmtpHost,
    int SmtpPort,
    MailServerSecurity SmtpSecurity,
    string Username)
{
    /// <summary>Trimmed, lower-cased hosts, and the first rule broken as a code.</summary>
    public Result<MailServerEndpoints> Validate()
    {
        var imapHost = NormalizeHost(ImapHost);
        var smtpHost = NormalizeHost(SmtpHost);
        var username = Username?.Trim() ?? string.Empty;

        if (imapHost is null) return Result.Fail<MailServerEndpoints>("MAIL_IMAP_HOST_INVALID");
        if (smtpHost is null) return Result.Fail<MailServerEndpoints>("MAIL_SMTP_HOST_INVALID");
        if (!MailServerSettings.ImapPorts.Contains(ImapPort)) return Result.Fail<MailServerEndpoints>("MAIL_IMAP_PORT_NOT_ALLOWED");
        if (!MailServerSettings.SmtpPorts.Contains(SmtpPort)) return Result.Fail<MailServerEndpoints>("MAIL_SMTP_PORT_NOT_ALLOWED");
        if (!Enum.IsDefined(ImapSecurity) || !Enum.IsDefined(SmtpSecurity)) return Result.Fail<MailServerEndpoints>("MAIL_SECURITY_INVALID");
        if (username.Length is 0 or > MailServerSettings.UsernameMaxLength) return Result.Fail<MailServerEndpoints>("MAIL_USERNAME_REQUIRED");

        return Result.Ok(this with { ImapHost = imapHost, SmtpHost = smtpHost, Username = username });
    }

    /// <summary>A DNS name or an address literal; a URL, a path or a port glued to it is refused rather than guessed at.</summary>
    private static string? NormalizeHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (trimmed.Length > MailServerSettings.HostMaxLength) return null;
        return Uri.CheckHostName(trimmed) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6 ? trimmed : null;
    }
}

/// <summary>
/// An e-mail template of the institution (connected mailbox, lot 3): what an agent starts a message
/// from. Variables are written <c>{{destinataire.nom}}</c> and filled when the agent picks the
/// template for a recipient (<c>MailTemplateRenderer</c>). Archived, never deleted: a template may
/// be what last week's messages were written from.
/// </summary>
public sealed class MailTemplate : AggregateRoot
{
    public const int NameMaxLength = 120;
    public const int SubjectMaxLength = 300;
    public const int BodyMaxLength = 100_000;

    private MailTemplate() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = default!;
    public string Subject { get; private set; } = default!;

    /// <summary>HTML, as the editor produced it.</summary>
    public string Body { get; private set; } = default!;

    public bool IsArchived { get; private set; }
    public Guid UpdatedBy { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Result<MailTemplate> Create(Guid tenantId, string name, string subject, string body, Guid actor, DateTimeOffset now)
    {
        var template = new MailTemplate { Id = Guid.NewGuid(), TenantId = tenantId };
        var applied = template.Update(name, subject, body, actor, now);
        return applied.IsSuccess ? Result.Ok(template) : Result.Fail<MailTemplate>(applied.Error!);
    }

    public Result Update(string name, string subject, string body, Guid actor, DateTimeOffset now)
    {
        name = name?.Trim() ?? string.Empty;
        subject = subject?.Trim() ?? string.Empty;
        body ??= string.Empty;

        if (name.Length is 0 or > NameMaxLength) return Result.Fail("MAIL_TEMPLATE_NAME_INVALID");
        if (subject.Length is 0 or > SubjectMaxLength) return Result.Fail("MAIL_TEMPLATE_SUBJECT_INVALID");
        if (string.IsNullOrWhiteSpace(body) || body.Length > BodyMaxLength) return Result.Fail("MAIL_TEMPLATE_BODY_INVALID");

        Name = name;
        Subject = subject;
        Body = body;
        UpdatedBy = actor;
        UpdatedAt = now;
        return Result.Ok();
    }

    public void Archive(bool archived, Guid actor, DateTimeOffset now)
    {
        IsArchived = archived;
        UpdatedBy = actor;
        UpdatedAt = now;
    }
}

public enum MailFolderKind
{
    Inbox,
    Sent,
}

public enum MailDirection
{
    Received,
    Sent,
}

/// <summary>
/// One e-mail read from a connected mailbox (connected mailbox, lot 2) — kept ONLY because one of
/// its addresses is a lead's or a client's (owner's decision): the rest of the mailbox is read,
/// matched and forgotten, never stored. Everything a person wrote or is named by is ENCRYPTED
/// (addresses, subject, preview, body, attachment names) and readable by the mailbox's owner
/// alone; a colleague only ever reaches <see cref="ConnectedMailLink"/> — that an exchange
/// happened, when, and which way.
///
/// <para>
/// Attachments are described, not stored: their bytes stay at the provider and are fetched when
/// opened. A body is capped at <see cref="BodyMaxLength"/> characters (<see cref="BodyTruncated"/>).
/// </para>
/// </summary>
public sealed class ConnectedMailMessage : AggregateRoot
{
    public const int BodyMaxLength = 500_000;

    private ConnectedMailMessage() { }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>The provider's id (Graph id, Gmail id, « inbox:uidvalidity:uid » for IMAP). Opaque.</summary>
    public string ExternalId { get; private set; } = default!;

    /// <summary>The Message-ID header — what a reply's In-Reply-To names (lot 3).</summary>
    public string? InternetMessageId { get; private set; }

    /// <summary>
    /// What groups a conversation: Graph conversationId, Gmail threadId, and for IMAP the first
    /// Message-ID of References (else In-Reply-To, else its own). Never shown.
    /// </summary>
    public string ThreadKey { get; private set; } = default!;

    public MailFolderKind Folder { get; private set; }
    public MailDirection Direction { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public bool IsRead { get; private set; }
    public bool HasAttachments { get; private set; }

    /// <summary>Ciphertext of the sender as JSON (<c>{a, n}</c>).</summary>
    public string? FromEnc { get; private set; }

    /// <summary>Ciphertext of the recipients as a JSON list.</summary>
    public string? ToEnc { get; private set; }

    public string? CcEnc { get; private set; }
    public string? SubjectEnc { get; private set; }
    public string? SnippetEnc { get; private set; }
    public string? BodyEnc { get; private set; }
    public bool BodyIsHtml { get; private set; }
    public bool BodyTruncated { get; private set; }

    /// <summary>Ciphertext of the attachments' descriptions as a JSON list — names can say a lot.</summary>
    public string? AttachmentsEnc { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ConnectedMailMessage Create(
        Guid tenantId, Guid accountId, Guid userId, string externalId, string? internetMessageId, string threadKey,
        MailFolderKind folder, MailDirection direction, DateTimeOffset sentAt, bool isRead, bool hasAttachments, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AccountId = accountId,
        UserId = userId,
        ExternalId = externalId,
        InternetMessageId = Clip(internetMessageId, 1000),
        ThreadKey = Clip(threadKey, 1000) ?? externalId,
        Folder = folder,
        Direction = direction,
        SentAt = sentAt,
        IsRead = isRead,
        HasAttachments = hasAttachments,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void SetContent(
        string? fromEnc, string? toEnc, string? ccEnc, string? subjectEnc, string? snippetEnc,
        string? bodyEnc, bool bodyIsHtml, bool bodyTruncated, string? attachmentsEnc)
    {
        FromEnc = fromEnc;
        ToEnc = toEnc;
        CcEnc = ccEnc;
        SubjectEnc = subjectEnc;
        SnippetEnc = snippetEnc;
        BodyEnc = bodyEnc;
        BodyIsHtml = bodyIsHtml;
        BodyTruncated = bodyTruncated;
        AttachmentsEnc = attachmentsEnc;
    }

    /// <summary>What the provider says changed on a message already kept: read or not, moved.</summary>
    public bool Refresh(bool isRead, MailFolderKind folder, DateTimeOffset now)
    {
        if (IsRead == isRead && Folder == folder) return false;
        IsRead = isRead;
        Folder = folder;
        UpdatedAt = now;
        return true;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

public enum MailLinkEntity
{
    Lead,
    Client,
}

/// <summary>
/// An e-mail exchanged with a lead or a client (lot 2): the part a colleague may see — when, which
/// way, whose mailbox — and nothing of what was written. One row per message and record; a message
/// with a lead and its client in copy has two.
/// </summary>
public sealed class ConnectedMailLink : AggregateRoot
{
    private ConnectedMailLink() { }

    public Guid MessageId { get; private set; }
    public MailLinkEntity EntityType { get; private set; }
    public Guid EntityId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid AccountId { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public MailDirection Direction { get; private set; }

    public static ConnectedMailLink For(ConnectedMailMessage message, MailLinkEntity type, Guid entityId) => new()
    {
        TenantId = message.TenantId,
        MessageId = message.Id,
        EntityType = type,
        EntityId = entityId,
        UserId = message.UserId,
        AccountId = message.AccountId,
        SentAt = message.SentAt,
        Direction = message.Direction,
    };
}

/// <summary>
/// Where the reading of one part of a mailbox stands (lot 2): a Graph delta link per folder, a
/// Gmail history id, an IMAP « uidvalidity:last uid » per folder. Opaque; null reads the window
/// (<see cref="Window"/>) from scratch.
/// </summary>
public sealed class MailboxSyncState : AggregateRoot
{
    /// <summary>How far back a first read goes. Older exchanges are not imported.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private MailboxSyncState() { }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }

    /// <summary>« inbox », « sent », or « all » for Gmail, whose history covers every label at once.</summary>
    public string Stream { get; private set; } = default!;

    public string? Cursor { get; private set; }
    public DateTimeOffset? LastSyncAt { get; private set; }
    public string? LastError { get; private set; }

    public static MailboxSyncState Start(Guid tenantId, Guid accountId, string stream) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        AccountId = accountId,
        Stream = stream,
    };

    public void Advance(string? cursor, DateTimeOffset now)
    {
        Cursor = cursor;
        LastSyncAt = now;
        LastError = null;
    }

    /// <summary>The provider forgot the cursor: the next run reads the window again (kept messages are matched by id).</summary>
    public void Reset() => Cursor = null;

    public void RecordFailure(string code) => LastError = code.Length <= 60 ? code : code[..60];
}
