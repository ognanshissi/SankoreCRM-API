namespace Sankore.Admin.Domain;

public enum SupportTicketStatus
{
    /// <summary>Opened by the tenant, nobody at the platform has touched it yet.</summary>
    New,

    /// <summary>The platform is working on it.</summary>
    InProgress,

    /// <summary>The platform asked the tenant something and waits for the answer.</summary>
    WaitingOnTenant,

    /// <summary>The platform considers it solved. Closes by itself after <see cref="SupportTicket.AutoCloseDelay"/>.</summary>
    Resolved,

    /// <summary>Over. Only an explicit reopening brings it back.</summary>
    Closed,
}

public enum SupportTicketCategory
{
    Bug,
    Question,
    Access,
    ModulesBilling,
    Suggestion,
}

public enum SupportTicketPriority
{
    Low,
    Normal,
    High,
    Blocking,
}

public enum SupportAuthorKind
{
    /// <summary>A user of the tenant.</summary>
    Tenant,

    /// <summary>A platform administrator.</summary>
    Platform,

    /// <summary>Nobody: the automatic closing.</summary>
    System,
}

public enum SupportMessageKind
{
    /// <summary>A message both sides read.</summary>
    Reply,

    /// <summary>A platform note. NEVER leaves the platform routes.</summary>
    InternalNote,

    /// <summary>A status change, kept in the thread so both sides see when and by whom.</summary>
    StatusChange,
}

/// <summary>
/// A request a tenant user addresses to the platform team, with its thread.
///
/// <para>
/// It lives in the registry, not in the tenant's database, because the platform reads every
/// tenant's tickets in one queue. Sankore.Api relays the tenant side with the service key and
/// names the user from its own token; the registry trusts that name and checks nothing else.
/// </para>
///
/// <para>
/// Transitions answer <c>false</c> when the ticket is not in a state they start from; the handler
/// turns that into a conflict. They never throw — this host maps no exception to a response.
/// </para>
/// </summary>
public sealed class SupportTicket
{
    public const int SubjectMaxLength = 200;
    public const int BodyMaxLength = 10000;
    public const int EmailMaxLength = 256;
    public const int NameMaxLength = 200;
    public const int RoleMaxLength = 100;
    public const int PageUrlMaxLength = 500;
    public const int AppVersionMaxLength = 50;
    public const int UserAgentMaxLength = 500;

    /// <summary>A Resolved ticket nobody answered closes by itself after this.</summary>
    public static readonly TimeSpan AutoCloseDelay = TimeSpan.FromDays(7);

    private readonly List<SupportMessage> _messages = [];

    public Guid Id { get; private set; }

    /// <summary>Sequential across the platform; shown as <see cref="Reference"/>.</summary>
    public long Number { get; private set; }

    /// <summary>Logical reference to <see cref="Tenant"/>; no physical FK, like TenantDomains.</summary>
    public Guid TenantId { get; private set; }

    public string Subject { get; private set; } = null!;
    public SupportTicketCategory Category { get; private set; }
    public SupportTicketPriority Priority { get; private set; }
    public SupportTicketStatus Status { get; private set; }

    public Guid RequesterUserId { get; private set; }
    public string RequesterEmail { get; private set; } = null!;
    public string RequesterName { get; private set; } = null!;

    /// <summary>The requester's role when they opened it, so the platform knows what they can see.</summary>
    public string? RequesterRole { get; private set; }

    /// <summary>The screen the ticket was opened from.</summary>
    public string? PageUrl { get; private set; }

    public string? AppVersion { get; private set; }
    public string? UserAgent { get; private set; }

    public Guid? AssignedAdminId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Last message or status change, either side: the order of both queues.</summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    public IReadOnlyList<SupportMessage> Messages => _messages;

    public string Reference => FormatReference(Number);

    public static string FormatReference(long number) => $"SUP-{number:D6}";

    private SupportTicket() { }

    public static SupportTicket Open(
        Guid tenantId, SupportActor requester, string requesterEmail, string? requesterRole,
        string subject, SupportTicketCategory category, SupportTicketPriority priority, string body,
        string? pageUrl, string? appVersion, string? userAgent, DateTimeOffset now)
    {
        var ticket = new SupportTicket
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Subject = subject.Trim(),
            Category = category,
            Priority = priority,
            Status = SupportTicketStatus.New,
            RequesterUserId = requester.Id!.Value,
            RequesterEmail = requesterEmail.Trim(),
            RequesterName = requester.Name,
            RequesterRole = Clip(requesterRole, RoleMaxLength),
            PageUrl = Clip(pageUrl, PageUrlMaxLength),
            AppVersion = Clip(appVersion, AppVersionMaxLength),
            UserAgent = Clip(userAgent, UserAgentMaxLength),
            CreatedAt = now,
            LastActivityAt = now,
        };
        ticket.Append(SupportMessageKind.Reply, requester, body, null, now);
        return ticket;
    }

    /// <summary>
    /// The tenant answers. Answering a ticket the platform waits on, or one it marked Resolved,
    /// puts it back in the platform's hands.
    /// </summary>
    public SupportMessage? TenantReply(SupportActor author, string body, DateTimeOffset now)
    {
        if (Status == SupportTicketStatus.Closed) return null;

        var message = Append(SupportMessageKind.Reply, author, body, null, now);
        if (Status is SupportTicketStatus.WaitingOnTenant or SupportTicketStatus.Resolved)
            MoveTo(SupportTicketStatus.InProgress, author, now);
        return message;
    }

    /// <summary>
    /// The platform answers or notes. A reply moves the ticket to <paramref name="nextStatus"/>,
    /// WaitingOnTenant when none is given — answering usually means the ball is in the tenant's
    /// court. An internal note changes nothing the tenant could notice.
    /// </summary>
    public SupportMessage? PlatformReply(SupportActor author, string body, bool isInternal,
        SupportTicketStatus? nextStatus, DateTimeOffset now)
    {
        if (Status == SupportTicketStatus.Closed) return null;

        if (isInternal)
            return Append(SupportMessageKind.InternalNote, author, body, null, now);

        var message = Append(SupportMessageKind.Reply, author, body, null, now);
        var target = nextStatus ?? SupportTicketStatus.WaitingOnTenant;
        if (target != Status) MoveTo(target, author, now);
        return message;
    }

    /// <summary>The platform sets any status, Closed and reopening included.</summary>
    public bool ChangeStatus(SupportActor author, SupportTicketStatus status, DateTimeOffset now)
    {
        if (status == Status) return false;

        MoveTo(status, author, now);
        return true;
    }

    /// <summary>The tenant closes its own ticket: the problem went away.</summary>
    public bool CloseByTenant(SupportActor author, DateTimeOffset now)
    {
        if (Status == SupportTicketStatus.Closed) return false;

        MoveTo(SupportTicketStatus.Closed, author, now);
        return true;
    }

    /// <summary>The tenant reopens a ticket the platform called solved or that was closed.</summary>
    public bool ReopenByTenant(SupportActor author, DateTimeOffset now)
    {
        if (Status is not (SupportTicketStatus.Resolved or SupportTicketStatus.Closed)) return false;

        MoveTo(SupportTicketStatus.InProgress, author, now);
        return true;
    }

    /// <summary>Closes a ticket left Resolved for <see cref="AutoCloseDelay"/> with no answer.</summary>
    public bool AutoClose(DateTimeOffset now)
    {
        if (Status != SupportTicketStatus.Resolved || ResolvedAt is not { } resolved
                                                    || now - resolved < AutoCloseDelay)
            return false;

        MoveTo(SupportTicketStatus.Closed, SupportActor.System, now);
        return true;
    }

    public void AssignTo(Guid? adminId) => AssignedAdminId = adminId;

    public bool ChangePriority(SupportTicketPriority priority)
    {
        if (priority == Priority) return false;
        Priority = priority;
        return true;
    }

    private void MoveTo(SupportTicketStatus status, SupportActor author, DateTimeOffset now)
    {
        Status = status;
        ResolvedAt = status == SupportTicketStatus.Resolved ? now : null;
        ClosedAt = status == SupportTicketStatus.Closed ? now : null;
        Append(SupportMessageKind.StatusChange, author, null, status, now);
    }

    private SupportMessage Append(SupportMessageKind kind, SupportActor author, string? body,
        SupportTicketStatus? status, DateTimeOffset now)
    {
        var message = SupportMessage.Create(Id, TenantId, kind, author, body?.Trim(), status, now);
        _messages.Add(message);
        LastActivityAt = now;
        return message;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>Who wrote a message: a tenant user, a platform administrator, or nobody (automatic).</summary>
public sealed record SupportActor(SupportAuthorKind Kind, Guid? Id, string Name)
{
    public static readonly SupportActor System = new(SupportAuthorKind.System, null, "Sankore");

    public static SupportActor TenantUser(Guid userId, string name) => new(SupportAuthorKind.Tenant, userId, name.Trim());
    public static SupportActor PlatformAdmin(Guid adminId, string name) => new(SupportAuthorKind.Platform, adminId, name.Trim());
}

/// <summary>One entry of a ticket's thread. Never edited, never deleted.</summary>
public sealed class SupportMessage
{
    public Guid Id { get; private set; }
    public Guid TicketId { get; private set; }

    /// <summary>Copied from the ticket so the activity feed of one tenant is one indexed read.</summary>
    public Guid TenantId { get; private set; }

    public SupportMessageKind Kind { get; private set; }
    public SupportAuthorKind AuthorKind { get; private set; }
    public Guid? AuthorId { get; private set; }
    public string AuthorName { get; private set; } = null!;

    /// <summary>Null for a status change.</summary>
    public string? Body { get; private set; }

    /// <summary>The status reached, for a status change only.</summary>
    public SupportTicketStatus? Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>What the tenant routes may return.</summary>
    public bool IsVisibleToTenant => Kind != SupportMessageKind.InternalNote;

    private SupportMessage() { }

    internal static SupportMessage Create(Guid ticketId, Guid tenantId, SupportMessageKind kind, SupportActor author,
        string? body, SupportTicketStatus? status, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TicketId = ticketId,
        TenantId = tenantId,
        Kind = kind,
        AuthorKind = author.Kind,
        AuthorId = author.Id,
        AuthorName = author.Name,
        Body = body,
        Status = status,
        CreatedAt = now,
    };
}
